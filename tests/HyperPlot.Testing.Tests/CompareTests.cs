namespace HyperPlot.Testing.Tests;

/// <summary>The comparison of two images in memory (design §10.1, §10 "画像の比較（S3）").</summary>
public sealed class CompareTests
{
    /// <summary>
    /// A channel that differs by exactly the tolerance is not over it, and by one more is, for each of R, G, B, and A.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 4 × 3 gray image; a tolerance of 5 a channel and 0 pixels.
    ///
    /// Steps:
    /// - For each channel, raise it at one pixel by 5, compare; then by 6, compare.
    ///
    /// Expected:
    /// - At 5: no pixel over, a match, and the largest difference 5. At 6: one pixel over, no match, and the largest 6.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Compare_ChannelAtAndOverTolerance_CountsOnlyOver(int channel)
    {
        var expected = Images.Solid(4, 3, 100, 100, 100, 100);
        var tolerance = new ImageTolerance(channel: 5, pixels: 0);

        var at = (byte[])expected.Clone();
        at[(5 * 4) + channel] = 105;
        var atTolerance = GoldenImage.Compare(at, expected, 4, 3, tolerance);
        Assert.Equal((0L, 5, true), (atTolerance.PixelsOver, atTolerance.MaxDelta, atTolerance.Matches));

        var over = (byte[])expected.Clone();
        over[(5 * 4) + channel] = 94;
        var overTolerance = GoldenImage.Compare(over, expected, 4, 3, tolerance);
        Assert.Equal((1L, 6, false), (overTolerance.PixelsOver, overTolerance.MaxDelta, overTolerance.Matches));
    }

    /// <summary>
    /// Exactly as many pixels over as the tolerance allows match, and one more does not; the result keeps the tolerance it
    /// was counted with.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An 8 × 8 image; a tolerance of 1 a channel and 3 pixels.
    ///
    /// Steps:
    /// - Change 3 pixels by 2 and compare; change a fourth and compare.
    ///
    /// Expected:
    /// - 3 over, a match; 4 over, no match. Both results hold the tolerance given.
    /// </remarks>
    [Fact]
    public void Compare_PixelsAtAndOverLimit_MatchesOnlyAtLimit()
    {
        var expected = Images.Gradient(8, 8);
        var actual = (byte[])expected.Clone();
        var tolerance = new ImageTolerance(channel: 1, pixels: 3);
        int[] pixels = [3, 20, 41, 63];
        foreach (var p in pixels[..3])
        {
            actual[(p * 4) + 1] = (byte)(actual[(p * 4) + 1] ^ 2);
        }

        var three = GoldenImage.Compare(actual, expected, 8, 8, tolerance);
        actual[(pixels[3] * 4) + 1] = (byte)(actual[(pixels[3] * 4) + 1] ^ 2);
        var four = GoldenImage.Compare(actual, expected, 8, 8, tolerance);

        Assert.Equal((3L, true), (three.PixelsOver, three.Matches));
        Assert.Equal((4L, false), (four.PixelsOver, four.Matches));
        Assert.Equal(tolerance, three.Tolerance);
        Assert.Equal(tolerance, four.Tolerance);
    }

    /// <summary>
    /// The first pixel over is the first in row order, not the one with the largest difference; the largest difference counts
    /// every pixel, also those within the tolerance.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 10 × 6 image; a tolerance of 3 a channel.
    ///
    /// Steps:
    /// - Change (7, 1) by 50, (2, 4) by 4, and (9, 0) by 3; compare.
    ///
    /// Expected:
    /// - 2 pixels over; the first at (7, 1), since (9, 0) is within the tolerance; the largest difference 50.
    /// </remarks>
    [Fact]
    public void Compare_SeveralOver_FirstInRowOrderAndLargest()
    {
        var expected = Images.Gradient(10, 6);
        var actual = (byte[])expected.Clone();
        actual[((1 * 10) + 7) * 4] = (byte)(actual[((1 * 10) + 7) * 4] + 50);
        actual[(((4 * 10) + 2) * 4) + 2] = (byte)(actual[(((4 * 10) + 2) * 4) + 2] + 4);
        actual[(((0 * 10) + 9) * 4) + 3] = (byte)(actual[(((0 * 10) + 9) * 4) + 3] - 3);

        var difference = GoldenImage.Compare(actual, expected, 10, 6, new ImageTolerance(3, 0));

        Assert.Equal(2, difference.PixelsOver);
        Assert.Equal((7, 1), (difference.FirstX, difference.FirstY));
        Assert.Equal(50, difference.MaxDelta);
    }

    /// <summary>Identical images match exactly, with no first pixel; <see cref="ImageTolerance.Exact"/> fails on a difference of 1.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 5 × 5 image.
    ///
    /// Steps:
    /// - Compare it with itself under Exact; change one channel by 1 and compare again.
    ///
    /// Expected:
    /// - Itself: 0 over, largest 0, first (-1, -1), a match. Changed by 1: 1 over, no match. Exact is (0, 0).
    /// </remarks>
    [Fact]
    public void Compare_Exact_FailsOnOne()
    {
        var expected = Images.Gradient(5, 5);
        var same = GoldenImage.Compare(expected, expected, 5, 5, ImageTolerance.Exact);
        var actual = (byte[])expected.Clone();
        actual[13]++;
        var one = GoldenImage.Compare(actual, expected, 5, 5, ImageTolerance.Exact);

        Assert.Equal((0L, 0, -1, -1, true), (same.PixelsOver, same.MaxDelta, same.FirstX, same.FirstY, same.Matches));
        Assert.Equal((1L, false), (one.PixelsOver, one.Matches));
        Assert.Equal(new ImageTolerance(0, 0), ImageTolerance.Exact);
    }

    /// <summary>The arguments of the comparison and the tolerance throw the exceptions design §10.1 names.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 2 × 2 image (16 bytes).
    ///
    /// Steps:
    /// - Make tolerances with a negative channel and negative pixels; compare with a short actual, a short expected, and
    ///   widths and heights of 0 and -1.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the tolerances; ArgumentException for the rest.
    /// </remarks>
    [Fact]
    public void Compare_BadArguments_Throw()
    {
        var image = Images.Solid(2, 2, 1, 2, 3, 4);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ImageTolerance(-1, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ImageTolerance(0, -1));
        _ = Assert.Throws<ArgumentException>(() => GoldenImage.Compare(image.AsSpan(1), image, 2, 2, ImageTolerance.Exact));
        _ = Assert.Throws<ArgumentException>(() => GoldenImage.Compare(image, image.AsSpan(4), 2, 2, ImageTolerance.Exact));
        _ = Assert.Throws<ArgumentException>(() => GoldenImage.Compare([], [], 0, 2, ImageTolerance.Exact));
        _ = Assert.Throws<ArgumentException>(() => GoldenImage.Compare([], [], 2, 0, ImageTolerance.Exact));
        _ = Assert.Throws<ArgumentException>(() => GoldenImage.Compare(image, image, -1, -4, ImageTolerance.Exact));
    }
}
