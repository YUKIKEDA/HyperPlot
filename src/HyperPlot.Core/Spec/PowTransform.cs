namespace HyperPlot;

/// <summary>sign(v) · |v|^k and its inverse sign(s) · |s|^(1/k) (design §4.2): increasing across 0 for negative values too.</summary>
/// <param name="exponent">k: finite and greater than 0.</param>
internal sealed class PowTransform(double exponent) : IScale
{
    /// <inheritdoc />
    public void Forward(ReadOnlySpan<double> values, Span<double> destination)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            destination[i] = Math.CopySign(Math.Pow(Math.Abs(v), exponent), v);
        }
    }

    /// <inheritdoc />
    public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var s = values[i];
            destination[i] = Math.CopySign(Math.Pow(Math.Abs(s), 1 / exponent), s);
        }
    }
}
