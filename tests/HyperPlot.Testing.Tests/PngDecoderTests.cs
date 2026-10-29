namespace HyperPlot.Testing.Tests;

/// <summary>The PNG decoder of <c>HyperPlot.Testing</c> (design §10.1): the form it reads, and the forms it rejects.</summary>
public sealed class PngDecoderTests
{
    /// <summary>A PNG of the writer's form reads back pixel for pixel, with its chunks in file order.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 7 × 5 image whose pixels all differ, in a PNG made by hand (8-bit RGBA, filter 0, CRCs of its own).
    ///
    /// Steps:
    /// - Decode it.
    ///
    /// Expected:
    /// - 7 × 5, the same pixels, chunks IHDR, IDAT, IEND.
    /// </remarks>
    [Fact]
    public void Decode_WritersForm_ReadsPixels()
    {
        var rgba = Images.Gradient(7, 5);

        var image = PngDecoder.Decode(Images.Png(7, 5, 8, 6, 0, Images.Rows(rgba, 7, 5, filter: 0)));

        Assert.Equal((7, 5), (image.Width, image.Height));
        Assert.Equal(rgba, image.Pixels);
        Assert.Equal(["IHDR", "IDAT", "IEND"], image.Chunks.Select(c => c.Type));
    }

    /// <summary>Another bit depth, color type, interlace, or row filter is rejected with the reason.</summary>
    /// <remarks>
    /// Preconditions:
    /// - PNGs made by hand, each valid but for one field.
    ///
    /// Steps:
    /// - Decode a 16-bit image, an RGB image (color type 2), an interlaced image, and an image with a row of filter 1.
    ///
    /// Expected:
    /// - InvalidDataException each time, its message naming the field.
    /// </remarks>
    [Theory]
    [InlineData(16, 6, 0, 0, "bit depth 16")]
    [InlineData(8, 2, 0, 0, "color type 2")]
    [InlineData(8, 6, 1, 0, "interlace 1")]
    [InlineData(8, 6, 0, 1, "filter 1")]
    public void Decode_OtherForm_Throws(byte bitDepth, byte colorType, byte interlace, byte filter, string reason)
    {
        var rows = Images.Rows(Images.Gradient(3, 3), 3, 3, filter);

        var exception = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Images.Png(3, 3, bitDepth, colorType, interlace, rows)));

        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A broken file is rejected with the reason: a bad signature, a CRC that does not match, a cut-off file.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A valid 3 × 3 PNG made by hand.
    ///
    /// Steps:
    /// - Decode it with its first byte changed; with a byte of its IHDR data changed; cut to 13 bytes (inside the IHDR frame); cut to 20
    ///   bytes (the frame whole, its data not); cut before its last 4 bytes (inside the IEND frame); and an empty file.
    ///
    /// Expected:
    /// - InvalidDataException each time, its message naming the signature, the CRC, the frame that is cut off, or the data a chunk says it holds.
    /// </remarks>
    [Fact]
    public void Decode_BrokenFile_Throws()
    {
        var png = Images.Png(3, 3, 8, 6, 0, Images.Rows(Images.Gradient(3, 3), 3, 3, filter: 0));

        var signature = (byte[])png.Clone();
        signature[0] = 0;
        var crc = (byte[])png.Clone();
        crc[8 + 8 + 2]++;

        Assert.Contains("signature", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(signature)).Message, StringComparison.Ordinal);
        Assert.Contains("CRC", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(crc)).Message, StringComparison.Ordinal);
        Assert.Contains("cut off", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png.AsSpan(0, 13))).Message, StringComparison.Ordinal);
        Assert.Contains("holds", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png.AsSpan(0, 20))).Message, StringComparison.Ordinal);
        Assert.Contains(
            "cut off",
            Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(png.AsSpan(0, png.Length - 4))).Message,
            StringComparison.Ordinal
        );
        Assert.Contains("signature", Assert.Throws<InvalidDataException>(() => PngDecoder.Decode([])).Message, StringComparison.Ordinal);
    }

    /// <summary>Image data that does not inflate to the size the header gives is rejected.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A PNG whose header says 4 × 3 and whose data is the rows of a 3 × 3 image.
    ///
    /// Steps:
    /// - Decode it.
    ///
    /// Expected:
    /// - InvalidDataException naming the size the data inflates to.
    /// </remarks>
    [Fact]
    public void Decode_ShortData_Throws()
    {
        var rows = Images.Rows(Images.Gradient(3, 3), 3, 3, filter: 0);

        var exception = Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Images.Png(4, 3, 8, 6, 0, rows)));

        Assert.Contains("inflates to", exception.Message, StringComparison.Ordinal);
    }
}
