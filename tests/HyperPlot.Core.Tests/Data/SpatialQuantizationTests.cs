namespace HyperPlot.Tests;

/// <summary>The quantization, the codes, and the layouts of a spatial tile index for two and three axes (design §3.4, row C3a).</summary>
public sealed class SpatialQuantizationTests
{
    /// <summary>The grid cell never decreases as the value grows; the ends of the range are the first and the last cell, and so are the values outside.</summary>
    /// <remarks>
    /// Preconditions:
    /// - An axis of <paramref name="bits"/> bits over the range [<paramref name="lower"/>, <paramref name="upper"/>]:
    ///   [−50, 50], [0, 1024], [1e-300, 3e-300], [−1e308, 1e308], and [−double.MaxValue, double.MaxValue] with 32 bits,
    ///   and [−50, 50] with 21 bits. The widths of the last two ranges are not finite as a double.
    /// - 100,000 seeded values from one width below the range to one width above it (kept finite), the two ends, the
    ///   values next to each end, and the two values next to each of 1,000 seeded ends of grid cells, sorted.
    ///
    /// Steps:
    /// - Compute the grid cell of each value in the sorted order, and by the formula of the design.
    /// - Compute the grid cells of −∞ and +∞.
    ///
    /// Expected:
    /// - No grid cell is less than the one before it.
    /// - The lower end and every value below it are cell 0; the upper end and every value above it are the last cell,
    ///   2^bits − 1.
    /// - More than 1,000 different cells occur, and each cell is the cell of the formula.
    /// - −∞ is cell 0 and +∞ is the last cell.
    /// </remarks>
    [Theory]
    [InlineData(-50.0, 50.0, 32)]
    [InlineData(0.0, 1024.0, 32)]
    [InlineData(1e-300, 3e-300, 32)]
    [InlineData(-1e308, 1e308, 32)]
    [InlineData(-double.MaxValue, double.MaxValue, 32)]
    [InlineData(-50.0, 50.0, 21)]
    public void CellOfDifference_GrowingValues_NeverDecreasesAndClampsAtTheEnds(double lower, double upper, int bits)
    {
        var axis = new SpatialAxis(new ScaleRange(0, lower, upper), bits);
        var last = (uint)((1UL << bits) - 1);
        var random = new Random(50);
        var half = (upper / 2) - (lower / 2);
        var values = new List<double>
        {
            lower,
            upper,
            Math.BitDecrement(lower),
            Math.BitIncrement(lower),
            Math.BitDecrement(upper),
            Math.BitIncrement(upper),
        };
        for (var i = 0; i < 100_000; i++)
        {
            // From one width below to one width above, computed in halves so that the widest range does not overflow.
            var value = ((lower / 2) + (((random.NextDouble() * 3) - 1) * half)) * 2;
            values.Add(Math.Clamp(value, -double.MaxValue, double.MaxValue));
        }

        for (var i = 0; i < 1_000; i++)
        {
            var end = ((lower / 2) + (random.NextInt64(1L << bits) / Math.Pow(2, bits) * half)) * 2;
            values.Add(Math.BitDecrement(end));
            values.Add(Math.BitIncrement(end));
        }

        values.Sort();
        uint before = 0;
        var different = 0;
        foreach (var value in values)
        {
            var cell = axis.CellOfDifference(value);
            if (cell < before || cell != SpatialReference.Cell(value, lower, upper, bits))
            {
                Assert.Fail(
                    $"The value {value:R} is cell {cell} after cell {before}; the formula gives {SpatialReference.Cell(value, lower, upper, bits)}."
                );
            }

            if ((value <= lower && cell != 0) || (value >= upper && cell != last))
            {
                Assert.Fail($"The value {value:R} at or outside an end of the range is cell {cell}.");
            }

            different += cell != before ? 1 : 0;
            before = cell;
        }

        Assert.True(different > 1_000, $"Only {different} different cells occurred.");
        Assert.Equal(0u, axis.CellOfDifference(double.NegativeInfinity));
        Assert.Equal(last, axis.CellOfDifference(double.PositiveInfinity));
    }

    /// <summary>On an axis whose two ends are equal every value is cell 0.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Axes of 32 bits over [7, 7] and [0, 0].
    ///
    /// Steps:
    /// - Compute the grid cells of the end, of values below and above it, of ±double.MaxValue, and of ±∞.
    ///
    /// Expected:
    /// - Every one is cell 0.
    /// </remarks>
    [Fact]
    public void CellOfDifference_EqualEnds_IsCellZero()
    {
        foreach (var end in (double[])[7, 0])
        {
            var axis = new SpatialAxis(new ScaleRange(0, end, end), 32);
            double[] values = [end, end - 1, end + 1, -double.MaxValue, double.MaxValue, double.NegativeInfinity, double.PositiveInfinity];
            foreach (var value in values)
            {
                Assert.Equal(0u, axis.CellOfDifference(value));
            }
        }
    }

    /// <summary>The grid cell of a column value is the cell of its difference from the origin, taken exactly for a long.</summary>
    /// <remarks>
    /// Preconditions:
    /// - An axis of 32 bits with the origin 639,000,000,000,000,000 (a tick count of the year 2026) over the
    ///   differences [0, 2^32].
    ///
    /// Steps:
    /// - Compute the grid cells of the long values origin + k for k = 0, 1, 2, 1,000, 2^31, 2^32 − 1, and 2^32, and of
    ///   origin − 1.
    ///
    /// Expected:
    /// - The cell of origin + k is k up to 2^32 − 1; origin + 2^32, the upper end, is the last cell 2^32 − 1.
    /// - origin − 1 is cell 0.
    /// - The origin of the axis is the origin of the range.
    /// </remarks>
    [Fact]
    public void CellOf_LongValuesNearALargeOrigin_AreOneCellApart()
    {
        const long Origin = 639_000_000_000_000_000;
        var axis = new SpatialAxis(new ScaleRange(Origin, 0, 4_294_967_296.0), 32);

        Assert.Equal(Origin, axis.Origin);
        foreach (var k in (long[])[0, 1, 2, 1_000, 1L << 31, (1L << 32) - 1])
        {
            Assert.Equal((uint)k, axis.CellOf(Origin + k));
        }

        Assert.Equal(uint.MaxValue, axis.CellOf(Origin + (1L << 32)));
        Assert.Equal(0u, axis.CellOf(Origin - 1));
    }

    /// <summary>The code of two axes interleaves their bits, x in the even places, and the axes are read back from it.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 10,000 seeded pairs of 32-bit grid cells, and the pairs of 0, 1, 2^31, and 2^32 − 1.
    ///
    /// Steps:
    /// - Interleave each pair, and compute its code a bit at a time.
    /// - Read both axes back from the code.
    /// - Take the cell of the code at the levels 0, 1, 8, 16, and 32 from the layout of two axes.
    ///
    /// Expected:
    /// - The two codes are equal, and the axes read back are the pair.
    /// - The cell of a level is the upper 2 × level bits of the code: 0 at level 0, and the code at level 32.
    /// </remarks>
    [Fact]
    public void Interleave_TwoAxes_AgreesWithTheBitsOneAtATime()
    {
        var random = new Random(51);
        uint[] ends = [0, 1, 1u << 31, uint.MaxValue];
        var pairs = new List<(uint X, uint Y)>();
        foreach (var x in ends)
        {
            foreach (var y in ends)
            {
                pairs.Add((x, y));
            }
        }

        for (var i = 0; i < 10_000; i++)
        {
            pairs.Add(((uint)random.NextInt64(1L << 32), (uint)random.NextInt64(1L << 32)));
        }

        var plane = SpatialLayout.Plane;
        foreach (var (x, y) in pairs)
        {
            var code = SpatialCode.Interleave(x, y);
            Assert.Equal(SpatialReference.Code(x, y), code);
            Assert.Equal(x, SpatialCode.Axis2(code, 0));
            Assert.Equal(y, SpatialCode.Axis2(code, 1));
            Assert.Equal(0UL, plane.CellOf(code, 0));
            Assert.Equal(code, plane.CellOf(code, 32));
            foreach (var level in (int[])[1, 8, 16])
            {
                Assert.Equal(code >> (64 - (2 * level)), plane.CellOf(code, level));
                Assert.Equal(SpatialReference.Code(x >> (32 - level), y >> (32 - level)), plane.CellOf(code, level));
            }
        }
    }

    /// <summary>The code of three axes interleaves their 21 bits, and the axes are read back from it.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 10,000 seeded triples of 21-bit grid cells, and the triples of 0, 1, 2^20, and 2^21 − 1.
    ///
    /// Steps:
    /// - Interleave each triple, and compute its code a bit at a time.
    /// - Read the three axes back from the code.
    /// - Take the cell of the code at the levels 0, 1, 5, 7, and 21 from the layout of three axes.
    ///
    /// Expected:
    /// - The two codes are equal and below 2^63, and the axes read back are the triple.
    /// - The cell of a level is the upper 3 × level bits of the 63 bits: 0 at level 0, and the code at level 21.
    /// </remarks>
    [Fact]
    public void Interleave_ThreeAxes_AgreesWithTheBitsOneAtATime()
    {
        var random = new Random(52);
        uint[] ends = [0, 1, 1u << 20, (1u << 21) - 1];
        var triples = new List<(uint X, uint Y, uint Z)>();
        foreach (var x in ends)
        {
            foreach (var y in ends)
            {
                foreach (var z in ends)
                {
                    triples.Add((x, y, z));
                }
            }
        }

        for (var i = 0; i < 10_000; i++)
        {
            triples.Add(((uint)random.Next(1 << 21), (uint)random.Next(1 << 21), (uint)random.Next(1 << 21)));
        }

        var space = SpatialLayout.Space;
        foreach (var (x, y, z) in triples)
        {
            var code = SpatialCode.Interleave(x, y, z);
            Assert.Equal(SpatialReference.Code(x, y, z), code);
            Assert.True(code < 1UL << 63);
            Assert.Equal(x, SpatialCode.Axis3(code, 0));
            Assert.Equal(y, SpatialCode.Axis3(code, 1));
            Assert.Equal(z, SpatialCode.Axis3(code, 2));
            foreach (var level in (int[])[0, 1, 5, 7, 21])
            {
                Assert.Equal(code >> (63 - (3 * level)), space.CellOf(code, level));
                Assert.Equal(SpatialReference.Code(x >> (21 - level), y >> (21 - level), z >> (21 - level)), space.CellOf(code, level));
            }
        }
    }

    /// <summary>The layouts hold the bits of an axis and the first level for two and for three axes.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The layouts of two and of three axes.
    ///
    /// Steps:
    /// - Read their numbers.
    ///
    /// Expected:
    /// - Two axes: 32 bits an axis, 64 bits in all, 4 children, the first level 8 with 65,536 cells, and 4 levels a pass.
    /// - Three axes: 21 bits an axis, 63 bits in all, 8 children, the first level 5 with 32,768 cells, and 2 levels a pass.
    /// - The bits below a level are the bits in all less the axes × the level.
    /// </remarks>
    [Fact]
    public void Layout_TwoAndThreeAxes_HoldsTheBitsOfTheDesign()
    {
        var plane = SpatialLayout.Plane;
        var space = SpatialLayout.Space;

        Assert.Equal(
            (2, 32, 64, 4, 8, 65_536, 4),
            (plane.Dimensions, plane.BitsPerAxis, plane.TotalBits, plane.ChildCount, plane.FirstLevel, plane.FirstCellCount, plane.MaxDescent)
        );
        Assert.Equal(
            (3, 21, 63, 8, 5, 32_768, 2),
            (space.Dimensions, space.BitsPerAxis, space.TotalBits, space.ChildCount, space.FirstLevel, space.FirstCellCount, space.MaxDescent)
        );
        Assert.Equal(64, plane.BitsBelow(0));
        Assert.Equal(48, plane.BitsBelow(8));
        Assert.Equal(0, plane.BitsBelow(32));
        Assert.Equal(63, space.BitsBelow(0));
        Assert.Equal(48, space.BitsBelow(5));
        Assert.Equal(0, space.BitsBelow(21));
    }

    /// <summary>Encode writes the codes of the finite points in their order and leaves the others out, for two and for three axes.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float x, double y, and int z values; every 7th x is NaN, every 11th y is +∞, and every 13th x is −∞.
    /// - Axes over [−50, 50], [0, 1000], and the differences [0, 2,000,000] from the origin −1,000,000: 32 bits for two
    ///   axes and 21 bits for three.
    ///
    /// Steps:
    /// - Encode x and y, and then x, y, and z.
    /// - Compute, by a loop over the rows with finite values, the code of each from the formula of the design a bit at a time.
    ///
    /// Expected:
    /// - Each call returns the number of rows with finite values, and its codes are the codes of the loop in the order of the rows.
    /// </remarks>
    [Fact]
    public void Encode_PointsWithNaNAndInfinity_WritesTheCodesOfTheFinitePoints()
    {
        const int Count = 5_000;
        var random = new Random(53);
        var xs = new float[Count];
        var ys = new double[Count];
        var zs = new int[Count];
        for (var i = 0; i < Count; i++)
        {
            xs[i] =
                i % 7 == 3 ? float.NaN
                : i % 13 == 5 ? float.NegativeInfinity
                : (random.NextSingle() * 100) - 50;
            ys[i] = i % 11 == 2 ? double.PositiveInfinity : random.NextDouble() * 1000;
            zs[i] = random.Next(-1_000_000, 1_000_000);
        }

        var expected2 = new List<ulong>();
        var expected3 = new List<ulong>();
        for (var i = 0; i < Count; i++)
        {
            if (float.IsFinite(xs[i]) && double.IsFinite(ys[i]))
            {
                expected2.Add(SpatialReference.Code(SpatialReference.Cell(xs[i], -50, 50), SpatialReference.Cell(ys[i], 0, 1000)));
                expected3.Add(
                    SpatialReference.Code(
                        SpatialReference.Cell(xs[i], -50, 50, 21),
                        SpatialReference.Cell(ys[i], 0, 1000, 21),
                        SpatialReference.Cell(zs[i] + 1_000_000.0, 0, 2_000_000, 21)
                    )
                );
            }
        }

        var codes2 = new ulong[Count];
        var codes3 = new ulong[Count];
        var count2 = SpatialCode.Encode<float, double>(xs, ys, Axis(-50, 50, 32), Axis(0, 1000, 32), codes2);
        var count3 = SpatialCode.Encode<float, double, int>(
            xs,
            ys,
            zs,
            Axis(-50, 50, 21),
            Axis(0, 1000, 21),
            new SpatialAxis(new ScaleRange(-1_000_000, 0, 2_000_000), 21),
            codes3
        );

        Assert.InRange(expected2.Count, 1, Count - 1);
        Assert.Equal(expected2.Count, count2);
        Assert.Equal(expected3.Count, count3);
        Assert.True(codes2.AsSpan(0, count2).SequenceEqual(expected2.ToArray()), "The codes of two axes differ from the loop.");
        Assert.True(codes3.AsSpan(0, count3).SequenceEqual(expected3.ToArray()), "The codes of three axes differ from the loop.");

        static SpatialAxis Axis(double lower, double upper, int bits) => new(new ScaleRange(0, lower, upper), bits);
    }
}
