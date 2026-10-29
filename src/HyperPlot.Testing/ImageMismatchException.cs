namespace HyperPlot.Testing;

/// <summary>
/// The exception that <see cref="GoldenImage.AssertMatches(ReadOnlySpan{byte}, int, int, string, ImageTolerance, string)"/>
/// throws when an image does not match its baseline, the baseline is missing, or the sizes differ (design §10.1).
/// </summary>
public sealed class ImageMismatchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ImageMismatchException"/> class.</summary>
    public ImageMismatchException() { }

    /// <summary>Initializes a new instance of the <see cref="ImageMismatchException"/> class with a message.</summary>
    /// <param name="message">The message.</param>
    public ImageMismatchException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="ImageMismatchException"/> class with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public ImageMismatchException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Initializes a new instance of the <see cref="ImageMismatchException"/> class with a message and the difference.</summary>
    /// <param name="message">The message.</param>
    /// <param name="difference">How the images differ, or null when there was no baseline or the sizes differ.</param>
    public ImageMismatchException(string message, ImageDifference? difference)
        : base(message) => Difference = difference;

    /// <summary>Gets how the images differ, or null when there was no baseline or the sizes differ.</summary>
    public ImageDifference? Difference { get; }
}
