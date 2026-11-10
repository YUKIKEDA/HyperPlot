namespace HyperPlot.Tests;

/// <summary>
/// The allocation seam of the index's copy for the tests: it records each request and the copy made for it, and can
/// throw instead of allocating or answer as an operating system without temporary files.
/// </summary>
internal sealed class RecordingCopyAllocator : SpatialCopyAllocator
{
    private readonly Lock _gate = new();
    private readonly List<Request> _requests = [];

    /// <summary>Gets the exception that every request throws, or <see langword="null"/> to allocate.</summary>
    public Exception? Failure { get; init; }

    /// <summary>Gets what the allocator answers for the kind of the temporary file, or <see langword="null"/> for the answer of the operating system.</summary>
    public TempFileKind? Kind { get; init; }

    /// <summary>Gets the requests so far, in the order they came.</summary>
    public Request[] Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc />
    public override TempFileKind TempFile => Kind ?? base.TempFile;

    /// <inheritdoc />
    public override SpatialCopy Allocate(long capacity, ReadOnlySpan<int> valueWidths, bool mapped, string? tempFolder)
    {
        var copy = Failure is null ? base.Allocate(capacity, valueWidths, mapped, tempFolder) : null;
        lock (_gate)
        {
            _requests.Add(new Request(capacity, [.. valueWidths], mapped, tempFolder, copy));
        }

        return copy ?? throw Failure!;
    }

    /// <summary>One request for a copy.</summary>
    /// <param name="Capacity">The number of points.</param>
    /// <param name="ValueWidths">The bytes of one x and of one y value.</param>
    /// <param name="Mapped">Whether a temporary file was asked for.</param>
    /// <param name="TempFolder">The folder that was passed.</param>
    /// <param name="Copy">The copy that was made, or <see langword="null"/> when the request threw.</param>
    internal sealed record Request(long Capacity, int[] ValueWidths, bool Mapped, string? TempFolder, SpatialCopy? Copy);
}
