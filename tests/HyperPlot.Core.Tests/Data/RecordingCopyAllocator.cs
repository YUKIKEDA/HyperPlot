namespace HyperPlot.Tests;

/// <summary>
/// The allocation seam of the index's copy for the tests: it records each request and the copy made for it, and can
/// throw instead of allocating, answer as an operating system without temporary files, or run the test's code each
/// time an allocation looks at whether its build was stopped.
/// </summary>
internal sealed class RecordingCopyAllocator : SpatialCopyAllocator
{
    private readonly Lock _gate = new();
    private readonly List<Request> _requests = [];

    /// <summary>Gets the exception that every request throws, or <see langword="null"/> to allocate.</summary>
    public Exception? Failure { get; init; }

    /// <summary>Gets what the allocator answers for the kind of the temporary file, or <see langword="null"/> for the answer of the operating system.</summary>
    public TempFileKind? Kind { get; init; }

    /// <summary>Gets what runs on the worker each time an allocation looks at whether its build was stopped, before the look.</summary>
    public Action? BeforeLook { get; init; }

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
    public override SpatialCopy Allocate(long capacity, ReadOnlySpan<int> valueWidths, bool mapped, string? tempFolder, Func<bool>? stopped = null)
    {
        Func<bool>? looked =
            stopped is null || BeforeLook is null
                ? stopped
                : () =>
                {
                    BeforeLook();
                    return stopped();
                };
        var copy = Failure is null ? base.Allocate(capacity, valueWidths, mapped, tempFolder, looked) : null;
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
