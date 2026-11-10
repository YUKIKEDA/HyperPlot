namespace HyperPlot;

/// <summary>
/// What decides a spatial tile index besides its two columns (design §3.4): the rows, each axis's transform, and each
/// axis's range. Two transforms are the same when they are the same object.
/// </summary>
internal readonly record struct SpatialIndexInput
{
    /// <summary>Gets the rows the index holds. The index holds the rows that are in the columns when the build starts.</summary>
    public required RowRange Rows { get; init; }

    /// <summary>Gets the transform of x, or <see langword="null"/> for none. The build calls it from worker threads.</summary>
    public IScale? XTransform { get; init; }

    /// <summary>Gets the transform of y, or <see langword="null"/> for none. The build calls it from worker threads.</summary>
    public IScale? YTransform { get; init; }

    /// <summary>Gets the range of x in the scale space, or <see langword="null"/> to take it from the finite values.</summary>
    public ScaleRange? XRange { get; init; }

    /// <summary>Gets the range of y in the scale space, or <see langword="null"/> to take it from the finite values.</summary>
    public ScaleRange? YRange { get; init; }

    /// <summary>
    /// Gets the folder of the temporary file that holds the copy of memory-mapped columns, or <see langword="null"/> for
    /// <see cref="Path.GetTempPath"/>.
    /// </summary>
    public string? TempFolder { get; init; }
}
