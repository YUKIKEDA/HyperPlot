namespace HyperPlot.Tests;

/// <summary>The evenly spaced column of integers, <see cref="Column.UniformInteger(long, long, long)"/> (design §3.1, row D2).</summary>
public sealed class UniformIntegerTests
{
    private const long Ticks2026 = 639_288_288_000_000_001;

    /// <summary>
    /// The column holds start + i * step exactly in long, gives the exact start and step through TryGetInteger, and its double
    /// members round the exact values once (design §3.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A column from 2026's .NET ticks + 1 (odd, which a double cannot hold), step 10, length 1,000; a column of doubles.
    ///
    /// Steps:
    /// - Read IsInteger, TryGetInteger, Start, Step, Last, and GetValue of the integer column; read IsInteger and TryGetInteger
    ///   of the column of doubles.
    ///
    /// Expected:
    /// - The integer column: IsInteger; TryGetInteger true with the exact start and step, whose start + 999 * step is the
    ///   exact last value; Start, Last, and GetValue(i) equal the exact values converted to double. The column of doubles: not
    ///   IsInteger; TryGetInteger false with 0 and 0.
    /// </remarks>
    [Fact]
    public void UniformInteger_Values_AreExactAndRoundOnceAsDoubles()
    {
        var column = Column.UniformInteger(Ticks2026, 10, 1000);

        Assert.True(column.IsInteger);
        Assert.True(column.TryGetInteger(out var start, out var step));
        Assert.Equal((Ticks2026, 10L), (start, step));
        Assert.Equal(Ticks2026 + 9990, start + (999 * step));
        Assert.Equal((double)Ticks2026, column.Start);
        Assert.Equal((double)(Ticks2026 + 9990), column.Last);
        Assert.Equal((double)(Ticks2026 + 50), column.GetValue(5));
        Assert.NotEqual(Ticks2026, (long)column.Start);

        var doubles = Column.Uniform(0.5, 0.25, 4);
        Assert.False(doubles.IsInteger);
        Assert.False(doubles.TryGetInteger(out start, out step));
        Assert.Equal((0L, 0L), (start, step));
    }

    /// <summary>The factory refuses a step that is not positive, a negative length, and a last value past long (design §3.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make columns with step 0 and -1; length -1; start long.MaxValue - 5 and step 2 with length 4 (last value past
    ///   long.MaxValue) and with length 3 (last value long.MaxValue - 1); length 0 at long.MaxValue.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException naming step, length, and length; the length 3 and the empty column are made, with the
    ///   exact last value.
    /// </remarks>
    [Fact]
    public void UniformInteger_OutOfRange_Throws()
    {
        Assert.Equal("step", Assert.Throws<ArgumentOutOfRangeException>(() => Column.UniformInteger(0, 0, 3)).ParamName);
        Assert.Equal("step", Assert.Throws<ArgumentOutOfRangeException>(() => Column.UniformInteger(0, -1, 3)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => Column.UniformInteger(0, 1, -1)).ParamName);
        Assert.Equal("length", Assert.Throws<ArgumentOutOfRangeException>(() => Column.UniformInteger(long.MaxValue - 5, 2, 4)).ParamName);
        var fits = Column.UniformInteger(long.MaxValue - 5, 2, 3);
        Assert.Equal(long.MaxValue - 1, fits.IntegerAt(2));
        Assert.Equal(0, Column.UniformInteger(long.MaxValue, 1, 0).Length);
    }

    /// <summary>
    /// A table refers to an integer uniform column as long and to a uniform column of doubles as double, and to neither the
    /// other way (design §3.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of an integer uniform column "ticks" and a uniform column of doubles "t".
    ///
    /// Steps:
    /// - Ask for each as long and as double.
    ///
    /// Expected:
    /// - Col&lt;long&gt;("ticks") and Col&lt;double&gt;("t") give references; Col&lt;double&gt;("ticks") and
    ///   Col&lt;long&gt;("t") throw ArgumentException naming the column's kind.
    /// </remarks>
    [Fact]
    public void Col_UniformColumns_AreReferencedByTheirKind()
    {
        var table = DataTable.Create().Add("ticks", Column.UniformInteger(Ticks2026, 10, 3)).Add("t", Column.Uniform(0, 1, 3)).Build();

        Assert.Equal(0, table.Col<long>("ticks").Index);
        Assert.Equal(1, table.Col<double>("t").Index);
        Assert.Contains(
            "UniformColumn (integer)",
            Assert.Throws<ArgumentException>(() => table.Col<double>("ticks")).Message,
            StringComparison.Ordinal
        );
        Assert.Contains("UniformColumn", Assert.Throws<ArgumentException>(() => table.Col<long>("t")).Message, StringComparison.Ordinal);
    }
}
