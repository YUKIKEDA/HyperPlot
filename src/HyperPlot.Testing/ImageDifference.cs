namespace HyperPlot.Testing;

/// <summary>How two images differ, counted with one tolerance (design §10.1).</summary>
/// <param name="Tolerance">The tolerance the pixels were counted with: the one given to <see cref="GoldenImage.Compare"/>.</param>
/// <param name="PixelsOver">The number of pixels with a channel that differs by more than <see cref="ImageTolerance.Channel"/>.</param>
/// <param name="MaxDelta">The largest difference of a channel over every pixel.</param>
/// <param name="FirstX">The column of the first pixel over the tolerance, in row order, or -1 if there is none.</param>
/// <param name="FirstY">The row of the first pixel over the tolerance, in row order, or -1 if there is none.</param>
public readonly record struct ImageDifference(ImageTolerance Tolerance, long PixelsOver, int MaxDelta, int FirstX, int FirstY)
{
    /// <summary>Gets a value indicating whether the images match: no more pixels over the tolerance than it allows.</summary>
    public bool Matches => PixelsOver <= Tolerance.Pixels;
}
