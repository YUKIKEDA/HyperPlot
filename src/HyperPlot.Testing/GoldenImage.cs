using System.Globalization;
using HyperPlot.Rendering;

namespace HyperPlot.Testing;

/// <summary>
/// Compares a drawn image with a baseline PNG within a tolerance (design §10.1). It depends on no test framework: a mismatch
/// throws <see cref="ImageMismatchException"/>, which any framework reports as a failure.
/// </summary>
public static class GoldenImage
{
    /// <summary>
    /// The environment variable that rewrites the baselines: when it is <c>1</c>, <c>AssertMatches</c> writes the image drawn
    /// as the baseline and does not compare.
    /// </summary>
    public const string UpdateVariable = "HYPERPLOT_UPDATE_GOLDEN";

    /// <summary>
    /// Compares two images: for each pixel, the largest difference of its four channels, and the pixels where it is more than
    /// <see cref="ImageTolerance.Channel"/>. It touches no file.
    /// </summary>
    /// <param name="actual">The image drawn: RGBA with the alpha taken out, 4 bytes a pixel, rows with no padding.</param>
    /// <param name="expected">The baseline, in the same form.</param>
    /// <param name="width">The width in pixels, 1 or more.</param>
    /// <param name="height">The height in pixels, 1 or more.</param>
    /// <param name="tolerance">The tolerance; the result keeps it, and <see cref="ImageDifference.Matches"/> uses it.</param>
    /// <returns>How the images differ.</returns>
    /// <exception cref="ArgumentException">A size is less than 1, or an image is not <c>width × height × 4</c> bytes.</exception>
    public static ImageDifference Compare(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, int width, int height, ImageTolerance tolerance)
    {
        CheckImage(actual, width, height, nameof(actual));
        CheckImage(expected, width, height, nameof(expected));
        long over = 0;
        var maxDelta = 0;
        var first = -1;
        var count = width * height;
        for (var p = 0; p < count; p++)
        {
            var i = p * 4;
            var delta = Math.Max(
                Math.Max(Math.Abs(actual[i] - expected[i]), Math.Abs(actual[i + 1] - expected[i + 1])),
                Math.Max(Math.Abs(actual[i + 2] - expected[i + 2]), Math.Abs(actual[i + 3] - expected[i + 3]))
            );
            maxDelta = Math.Max(maxDelta, delta);
            if (delta > tolerance.Channel)
            {
                if (over == 0)
                {
                    first = p;
                }

                over++;
            }
        }

        return first < 0
            ? new ImageDifference(tolerance, over, maxDelta, -1, -1)
            : new ImageDifference(tolerance, over, maxDelta, first % width, first / width);
    }

    /// <summary>
    /// Compares an image with its baseline PNG. When they do not match, it writes <c>&lt;name&gt;.actual.png</c> and
    /// <c>&lt;name&gt;.diff.png</c> into <paramref name="outputDirectory"/> (the name is the baseline's file name without its
    /// extension) and throws. With <see cref="UpdateVariable"/> set to <c>1</c>, it writes the image as the baseline instead.
    /// </summary>
    /// <param name="actual">The image drawn: RGBA with the alpha taken out, 4 bytes a pixel, rows with no padding.</param>
    /// <param name="width">The width in pixels, 1 or more.</param>
    /// <param name="height">The height in pixels, 1 or more.</param>
    /// <param name="baselinePath">The baseline PNG.</param>
    /// <param name="tolerance">The tolerance.</param>
    /// <param name="outputDirectory">The folder for the actual and diff images; it is made when missing.</param>
    /// <exception cref="ImageMismatchException">The images do not match, the baseline is missing, or the sizes differ.</exception>
    /// <exception cref="InvalidDataException">The baseline is not a PNG this library writes, or is broken.</exception>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">A size is less than 1, or the image is not <c>width × height × 4</c> bytes.</exception>
    public static void AssertMatches(
        ReadOnlySpan<byte> actual,
        int width,
        int height,
        string baselinePath,
        ImageTolerance tolerance,
        string outputDirectory
    )
    {
        ArgumentNullException.ThrowIfNull(baselinePath);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        CheckImage(actual, width, height, nameof(actual));
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            WritePng(baselinePath, actual, width, height);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(baselinePath);
        var actualPath = Path.Combine(outputDirectory, name + ".actual.png");
        if (!File.Exists(baselinePath))
        {
            WritePng(actualPath, actual, width, height);
            throw new ImageMismatchException(
                $"There is no baseline at {baselinePath}. The image drawn is at {actualPath}; "
                    + $"run with {UpdateVariable}=1 to write it as the baseline.",
                difference: null
            );
        }

        var baseline = PngDecoder.Decode(File.ReadAllBytes(baselinePath));
        if (baseline.Width != width || baseline.Height != height)
        {
            WritePng(actualPath, actual, width, height);
            throw new ImageMismatchException(
                $"The image drawn is {width} × {height} and the baseline {baselinePath} is {baseline.Width} × {baseline.Height}. "
                    + $"The image drawn is at {actualPath}.",
                difference: null
            );
        }

        var difference = Compare(actual, baseline.Pixels, width, height, tolerance);
        if (difference.Matches)
        {
            return;
        }

        var diffPath = Path.Combine(outputDirectory, name + ".diff.png");
        WritePng(actualPath, actual, width, height);
        WritePng(diffPath, DiffImage(actual, baseline.Pixels, tolerance.Channel), width, height);
        var counts = string.Create(
            CultureInfo.InvariantCulture,
            $"{difference.PixelsOver} pixels differ by more than {tolerance.Channel} in a channel, and {tolerance.Pixels} may."
        );
        var largest = string.Create(
            CultureInfo.InvariantCulture,
            $"The largest difference is {difference.MaxDelta}; the first pixel over is at ({difference.FirstX}, {difference.FirstY})."
        );
        var files = $"The image drawn is at {actualPath}, the difference at {diffPath}.";
        throw new ImageMismatchException($"The image does not match {baselinePath}: {counts} {largest} {files}", difference);
    }

    /// <summary>
    /// Reads what <paramref name="renderer"/> drew and compares it with its baseline PNG, as
    /// <see cref="AssertMatches(ReadOnlySpan{byte}, int, int, string, ImageTolerance, string)"/> does. It allocates one image.
    /// </summary>
    /// <param name="renderer">The renderer to read.</param>
    /// <param name="baselinePath">The baseline PNG.</param>
    /// <param name="tolerance">The tolerance.</param>
    /// <param name="outputDirectory">The folder for the actual and diff images; it is made when missing.</param>
    /// <exception cref="ImageMismatchException">The images do not match, the baseline is missing, or the sizes differ.</exception>
    /// <exception cref="InvalidDataException">The baseline is not a PNG this library writes, or is broken.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="renderer"/> or a path is null.</exception>
    public static void AssertMatches(HeadlessRenderer renderer, string baselinePath, ImageTolerance tolerance, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var pixels = new byte[renderer.Width * renderer.Height * 4];
        renderer.ReadPixels(pixels);
        AssertMatches(pixels, renderer.Width, renderer.Height, baselinePath, tolerance, outputDirectory);
    }

    // Pixels over the tolerance are opaque red; the others are the baseline's luminance, faded toward white, so that the red
    // stands out where the picture is (design §10.1).
    internal static byte[] DiffImage(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, int channel)
    {
        var diff = new byte[actual.Length];
        for (var i = 0; i < actual.Length; i += 4)
        {
            var delta = Math.Max(
                Math.Max(Math.Abs(actual[i] - expected[i]), Math.Abs(actual[i + 1] - expected[i + 1])),
                Math.Max(Math.Abs(actual[i + 2] - expected[i + 2]), Math.Abs(actual[i + 3] - expected[i + 3]))
            );
            if (delta > channel)
            {
                diff[i] = 255;
                diff[i + 1] = 0;
                diff[i + 2] = 0;
            }
            else
            {
                // Rec. 709 luminance, rounded to 8 bits, then a quarter of its distance from white.
                var luminance = ((2126 * expected[i]) + (7152 * expected[i + 1]) + (722 * expected[i + 2]) + 5000) / 10000;
                var gray = (byte)(255 - ((255 - luminance) / 4));
                diff[i] = gray;
                diff[i + 1] = gray;
                diff[i + 2] = gray;
            }

            diff[i + 3] = 255;
        }

        return diff;
    }

    private static void CheckImage(ReadOnlySpan<byte> image, int width, int height, string name)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentException($"The image must be at least 1 × 1; it is {width} × {height}.", name);
        }

        if (image.Length != (long)width * height * 4)
        {
            throw new ArgumentException($"The image holds {image.Length} bytes; {width} × {height} RGBA needs {(long)width * height * 4}.", name);
        }
    }

    private static void WritePng(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        using var file = File.Create(path);
        PngWriter.Write(file, rgba, width, height);
    }
}
