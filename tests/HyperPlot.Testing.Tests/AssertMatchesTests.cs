namespace HyperPlot.Testing.Tests;

/// <summary>
/// The tests that read or set <see cref="GoldenImage.UpdateVariable"/>. The variable belongs to the process, so these run
/// alone, after the tests that run in parallel.
/// </summary>
[CollectionDefinition(nameof(UpdateVariableTests), DisableParallelization = true)]
public sealed class UpdateVariableTests;

/// <summary>Comparing with a baseline file (design §10.1, §10 "画像の比較（S3）").</summary>
[Collection(nameof(UpdateVariableTests))]
public sealed class AssertMatchesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hyperplot-testing-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Initializes a new instance of the <see cref="AssertMatchesTests"/> class, with the update variable unset.</summary>
    public AssertMatchesTests() => Environment.SetEnvironmentVariable(GoldenImage.UpdateVariable, null);

    private string Baseline => Path.Combine(_folder, "Golden", "scene.png");

    private string Output => Path.Combine(_folder, "out");

    /// <inheritdoc />
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GoldenImage.UpdateVariable, null);
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>An image within the tolerance passes and writes nothing.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A baseline of a 6 × 4 image; the same image with one channel changed by 2.
    ///
    /// Steps:
    /// - Assert with a tolerance of 2 a channel.
    ///
    /// Expected:
    /// - No exception; the output folder is not made.
    /// </remarks>
    [Fact]
    public void AssertMatches_WithinTolerance_Passes()
    {
        var expected = Images.Gradient(6, 4);
        WriteBaseline(expected, 6, 4);
        var actual = (byte[])expected.Clone();
        actual[9] += 2;

        GoldenImage.AssertMatches(actual, 6, 4, Baseline, new ImageTolerance(2, 0), Output);

        Assert.False(Directory.Exists(Output));
    }

    /// <summary>
    /// With no baseline, the image drawn is written to the output and the call throws; the baseline is not made.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - No baseline file.
    ///
    /// Steps:
    /// - Assert a 3 × 2 image.
    ///
    /// Expected:
    /// - ImageMismatchException with no difference and a message naming the baseline and the update variable;
    ///   <c>scene.actual.png</c> holds the image; no diff; the baseline does not exist.
    /// </remarks>
    [Fact]
    public void AssertMatches_NoBaseline_WritesActualOnlyAndThrows()
    {
        var actual = Images.Gradient(3, 2);

        var exception = Assert.Throws<ImageMismatchException>(() => GoldenImage.AssertMatches(actual, 3, 2, Baseline, ImageTolerance.Exact, Output));

        Assert.Null(exception.Difference);
        Assert.Contains(Baseline, exception.Message, StringComparison.Ordinal);
        Assert.Contains(GoldenImage.UpdateVariable, exception.Message, StringComparison.Ordinal);
        Assert.Equal(actual, PngDecoder.Decode(File.ReadAllBytes(Path.Combine(Output, "scene.actual.png"))).Pixels);
        Assert.False(File.Exists(Path.Combine(Output, "scene.diff.png")));
        Assert.False(File.Exists(Baseline));
    }

    /// <summary>A baseline of another size throws with both sizes, writes the image drawn, and no diff.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 4 × 3 baseline.
    ///
    /// Steps:
    /// - Assert an image that differs in width only (5 × 3), in height only (4 × 5), and in both with the same pixel count
    ///   (6 × 2).
    ///
    /// Expected:
    /// - ImageMismatchException with no difference, naming both sizes; <c>scene.actual.png</c> and no diff.
    /// </remarks>
    [Theory]
    [InlineData(5, 3)]
    [InlineData(4, 5)]
    [InlineData(6, 2)]
    public void AssertMatches_OtherSize_ThrowsWithBothSizes(int width, int height)
    {
        WriteBaseline(Images.Gradient(4, 3), 4, 3);

        var exception = Assert.Throws<ImageMismatchException>(() =>
            GoldenImage.AssertMatches(Images.Gradient(width, height), width, height, Baseline, ImageTolerance.Exact, Output)
        );

        Assert.Null(exception.Difference);
        Assert.Contains($"{width} × {height}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("4 × 3", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Output, "scene.actual.png")));
        Assert.False(File.Exists(Path.Combine(Output, "scene.diff.png")));
    }

    /// <summary>
    /// A mismatch writes the image drawn and the diff, and throws with the difference: the diff is red where a pixel is over
    /// the tolerance and the baseline's faded luminance elsewhere, also where a pixel differs within the tolerance.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 4 × 2 baseline of one color (R 200, G 100, B 50); a tolerance of 3 a channel.
    ///
    /// Steps:
    /// - Change pixel (1, 0) by 10 in blue and pixel (2, 1) by 3 in red; assert.
    ///
    /// Expected:
    /// - ImageMismatchException with 1 pixel over, first at (1, 0), largest 10, and the tolerance given; the message names
    ///   the two files; the actual image reads back as drawn; the diff is (255, 0, 0, 255) at (1, 0) and gray
    ///   255 − (255 − Y) / 4 elsewhere, Y = (2126·200 + 7152·100 + 722·50 + 5000) / 10000 = 118, so 221.
    /// </remarks>
    [Fact]
    public void AssertMatches_Mismatch_WritesActualAndDiff()
    {
        var expected = Images.Solid(4, 2, 200, 100, 50, 255);
        WriteBaseline(expected, 4, 2);
        var actual = (byte[])expected.Clone();
        actual[(1 * 4) + 2] += 10;
        actual[((1 * 4) + 2) * 4] -= 3;
        var tolerance = new ImageTolerance(3, 0);

        var exception = Assert.Throws<ImageMismatchException>(() => GoldenImage.AssertMatches(actual, 4, 2, Baseline, tolerance, Output));

        var difference = Assert.NotNull(exception.Difference);
        Assert.Equal(new ImageDifference(tolerance, 1, 10, 1, 0), difference);
        var actualPath = Path.Combine(Output, "scene.actual.png");
        var diffPath = Path.Combine(Output, "scene.diff.png");
        Assert.Contains(actualPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(diffPath, exception.Message, StringComparison.Ordinal);
        Assert.Equal(actual, PngDecoder.Decode(File.ReadAllBytes(actualPath)).Pixels);
        var diff = PngDecoder.Decode(File.ReadAllBytes(diffPath)).Pixels;
        for (var p = 0; p < 8; p++)
        {
            byte[] pixel = p == 1 ? [255, 0, 0, 255] : [221, 221, 221, 255];
            Assert.Equal(pixel, diff[(p * 4)..((p * 4) + 4)]);
        }
    }

    /// <summary>With the update variable 1, the baseline is rewritten with the image drawn and the call passes.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A baseline that differs from the image everywhere; <c>HYPERPLOT_UPDATE_GOLDEN=1</c>.
    ///
    /// Steps:
    /// - Assert with Exact; then unset the variable and assert again.
    ///
    /// Expected:
    /// - No exception either time; the baseline reads back as the image; nothing is written to the output.
    /// </remarks>
    [Fact]
    public void AssertMatches_UpdateOne_RewritesBaseline()
    {
        WriteBaseline(Images.Solid(5, 5, 9, 9, 9, 9), 5, 5);
        var actual = Images.Gradient(5, 5);
        Environment.SetEnvironmentVariable(GoldenImage.UpdateVariable, "1");

        GoldenImage.AssertMatches(actual, 5, 5, Baseline, ImageTolerance.Exact, Output);
        Environment.SetEnvironmentVariable(GoldenImage.UpdateVariable, null);
        GoldenImage.AssertMatches(actual, 5, 5, Baseline, ImageTolerance.Exact, Output);

        Assert.Equal(actual, PngDecoder.Decode(File.ReadAllBytes(Baseline)).Pixels);
        Assert.False(Directory.Exists(Output));
    }

    /// <summary>The update variable at a value other than 1 compares, and leaves the baseline as it was; it makes none either.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A baseline that differs from the image.
    ///
    /// Steps:
    /// - With the variable at "0", at "true", at " 1", and at "" (which unsets it), assert; then, with "0", assert against a missing baseline.
    ///
    /// Expected:
    /// - ImageMismatchException each time; the baseline's bytes are unchanged; the missing one is not made.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("")]
    public void AssertMatches_UpdateNotOne_Compares(string value)
    {
        WriteBaseline(Images.Solid(5, 5, 9, 9, 9, 9), 5, 5);
        var before = File.ReadAllBytes(Baseline);
        Environment.SetEnvironmentVariable(GoldenImage.UpdateVariable, value);

        _ = Assert.Throws<ImageMismatchException>(() =>
            GoldenImage.AssertMatches(Images.Gradient(5, 5), 5, 5, Baseline, ImageTolerance.Exact, Output)
        );
        var missing = Path.Combine(_folder, "Golden", "missing.png");
        _ = Assert.Throws<ImageMismatchException>(() =>
            GoldenImage.AssertMatches(Images.Gradient(5, 5), 5, 5, missing, ImageTolerance.Exact, Output)
        );

        Assert.Equal(before, File.ReadAllBytes(Baseline));
        Assert.False(File.Exists(missing));
    }

    /// <summary>A broken baseline throws the decoder's InvalidDataException as it is, not an ImageMismatchException.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A baseline file whose last byte (in the IEND CRC) is changed.
    ///
    /// Steps:
    /// - Assert an image against it.
    ///
    /// Expected:
    /// - InvalidDataException naming the CRC.
    /// </remarks>
    [Fact]
    public void AssertMatches_BrokenBaseline_ThrowsInvalidData()
    {
        WriteBaseline(Images.Gradient(2, 2), 2, 2);
        var bytes = File.ReadAllBytes(Baseline);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(Baseline, bytes);

        var exception = Assert.Throws<InvalidDataException>(() =>
            GoldenImage.AssertMatches(Images.Gradient(2, 2), 2, 2, Baseline, ImageTolerance.Exact, Output)
        );

        Assert.Contains("CRC", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Null paths and a null renderer throw ArgumentNullException; a short image throws ArgumentException.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 2 × 2 image.
    ///
    /// Steps:
    /// - Assert with a null baseline path, a null output folder, a null renderer, and an image one byte short.
    ///
    /// Expected:
    /// - ArgumentNullException three times, then ArgumentException; nothing is written.
    /// </remarks>
    [Fact]
    public void AssertMatches_BadArguments_Throw()
    {
        var image = Images.Gradient(2, 2);
        _ = Assert.Throws<ArgumentNullException>(() => GoldenImage.AssertMatches(image, 2, 2, null!, ImageTolerance.Exact, Output));
        _ = Assert.Throws<ArgumentNullException>(() => GoldenImage.AssertMatches(image, 2, 2, Baseline, ImageTolerance.Exact, null!));
        _ = Assert.Throws<ArgumentNullException>(() =>
            GoldenImage.AssertMatches((Rendering.HeadlessRenderer)null!, Baseline, ImageTolerance.Exact, Output)
        );
        _ = Assert.Throws<ArgumentException>(() => GoldenImage.AssertMatches(image.AsSpan(1), 2, 2, Baseline, ImageTolerance.Exact, Output));
        Assert.False(Directory.Exists(_folder));
    }

    private void WriteBaseline(byte[] rgba, int width, int height)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Baseline)!);
        File.WriteAllBytes(Baseline, Images.Png(width, height, 8, 6, 0, Images.Rows(rgba, width, height, filter: 0)));
    }
}
