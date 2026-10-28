namespace HyperPlot.Rendering;

/// <summary>
/// The integer forms of premultiplying a color and taking the alpha out again (design §5.2). Exactly one half rounds up,
/// so that every backend and every test gets the same bytes.
/// </summary>
internal static class Premultiplied
{
    /// <summary>Returns <c>(c · a + 127) / 255</c>: the channel premultiplied by the alpha. It is never exactly one half.</summary>
    /// <param name="channel">The channel, not premultiplied.</param>
    /// <param name="alpha">The alpha.</param>
    /// <returns>The premultiplied channel.</returns>
    internal static byte Multiply(byte channel, byte alpha) => (byte)(((channel * alpha) + 127) / 255);

    /// <summary>
    /// Returns <c>(k · 255 + a / 2) / a</c>, at most 255: the channel with the alpha taken out, one half rounded up; 0 when
    /// <paramref name="alpha"/> is 0.
    /// </summary>
    /// <param name="channel">The premultiplied channel.</param>
    /// <param name="alpha">The alpha.</param>
    /// <returns>The channel, not premultiplied.</returns>
    internal static byte Divide(byte channel, byte alpha) => alpha == 0 ? (byte)0 : (byte)Math.Min(255, ((channel * 255) + (alpha / 2)) / alpha);
}
