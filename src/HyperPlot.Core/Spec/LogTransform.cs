namespace HyperPlot;

/// <summary>log10(v) and its inverse 10^s (design §4.2). 0 maps to −∞ and a negative value to NaN.</summary>
internal sealed class LogTransform : IScale
{
    /// <summary>Gets the one instance: it holds no state.</summary>
    public static LogTransform Instance { get; } = new();

    /// <inheritdoc />
    public void Forward(ReadOnlySpan<double> values, Span<double> destination)
    {
        for (var i = 0; i < values.Length; i++)
        {
            destination[i] = Math.Log10(values[i]);
        }
    }

    /// <inheritdoc />
    public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
    {
        for (var i = 0; i < values.Length; i++)
        {
            destination[i] = Math.Pow(10, values[i]);
        }
    }
}
