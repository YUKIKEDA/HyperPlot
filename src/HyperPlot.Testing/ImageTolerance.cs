namespace HyperPlot.Testing;

/// <summary>
/// How far an image may differ from its baseline (design §10.1): each channel of a pixel by at most <see cref="Channel"/>,
/// and at most <see cref="Pixels"/> pixels by more than that.
/// </summary>
public readonly struct ImageTolerance : IEquatable<ImageTolerance>
{
    /// <summary>Initializes a new instance of the <see cref="ImageTolerance"/> struct.</summary>
    /// <param name="channel">The largest difference of a channel (R, G, B, or A) that a pixel may have, 0 or more.</param>
    /// <param name="pixels">The number of pixels that may differ by more than <paramref name="channel"/>, 0 or more.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> or <paramref name="pixels"/> is negative.</exception>
    public ImageTolerance(int channel, long pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfNegative(pixels);
        Channel = channel;
        Pixels = pixels;
    }

    /// <summary>Gets the tolerance of no difference at all: (0, 0).</summary>
    public static ImageTolerance Exact => default;

    /// <summary>Gets the largest difference of a channel (R, G, B, or A) that a pixel may have.</summary>
    public int Channel { get; }

    /// <summary>Gets the number of pixels that may differ by more than <see cref="Channel"/>.</summary>
    public long Pixels { get; }

    /// <summary>Returns whether two tolerances are the same.</summary>
    /// <param name="left">The first tolerance.</param>
    /// <param name="right">The second tolerance.</param>
    /// <returns><see langword="true"/> if both values are the same.</returns>
    public static bool operator ==(ImageTolerance left, ImageTolerance right) => left.Equals(right);

    /// <summary>Returns whether two tolerances differ.</summary>
    /// <param name="left">The first tolerance.</param>
    /// <param name="right">The second tolerance.</param>
    /// <returns><see langword="true"/> if a value differs.</returns>
    public static bool operator !=(ImageTolerance left, ImageTolerance right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(ImageTolerance other) => Channel == other.Channel && Pixels == other.Pixels;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ImageTolerance other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Channel, Pixels);

    /// <inheritdoc />
    public override string ToString() => $"{Channel} a channel, {Pixels} pixels over it";
}
