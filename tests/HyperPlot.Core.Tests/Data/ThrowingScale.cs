namespace HyperPlot.Tests;

/// <summary>A user's transform that throws while it has an exception, and returns the values unchanged while it has none.</summary>
/// <param name="thrown">The exception to throw from the first call on, or <see langword="null"/> to start as the identity.</param>
internal sealed class ThrowingScale(Exception? thrown = null) : IScale
{
    private Exception? _thrown = thrown;

    /// <summary>Gets or sets the exception that both directions throw. A worker reads it at each call.</summary>
    public Exception? Thrown
    {
        get => Volatile.Read(ref _thrown);
        set => Volatile.Write(ref _thrown, value);
    }

    /// <inheritdoc />
    public void Forward(ReadOnlySpan<double> values, Span<double> destination) => Pass(values, destination);

    /// <inheritdoc />
    public void Inverse(ReadOnlySpan<double> values, Span<double> destination) => Pass(values, destination);

    private void Pass(ReadOnlySpan<double> values, Span<double> destination)
    {
        if (Thrown is { } exception)
        {
            throw exception;
        }

        values.CopyTo(destination);
    }
}
