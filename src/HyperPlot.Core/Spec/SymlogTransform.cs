namespace HyperPlot;

/// <summary>sign(v) · log10(1 + |v| / C) and its inverse sign(s) · C · (10^|s| − 1) (design §4.2): continuous across 0.</summary>
/// <param name="constant">C: finite and greater than 0.</param>
internal sealed class SymlogTransform(double constant) : IScale
{
    /// <inheritdoc />
    public void Forward(ReadOnlySpan<double> values, Span<double> destination)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            destination[i] = Math.CopySign(Math.Log10(1 + (Math.Abs(v) / constant)), v);
        }
    }

    /// <inheritdoc />
    public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var s = values[i];
            destination[i] = Math.CopySign(constant * (Math.Pow(10, Math.Abs(s)) - 1), s);
        }
    }
}
