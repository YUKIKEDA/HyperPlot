namespace HyperPlot;

/// <summary>A color in sRGB, 8 bits a channel, with an alpha that is not premultiplied (design §5.2).</summary>
/// <param name="R">The red channel.</param>
/// <param name="G">The green channel.</param>
/// <param name="B">The blue channel.</param>
/// <param name="A">The opacity: 0 is transparent, 255 opaque.</param>
public readonly record struct Color(byte R, byte G, byte B, byte A);
