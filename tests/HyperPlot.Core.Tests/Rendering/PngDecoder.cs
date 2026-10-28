using System.Buffers.Binary;
using System.IO.Compression;

namespace HyperPlot.Tests;

/// <summary>
/// A PNG reader for the tests, written apart from the writer: it checks the signature and every chunk's CRC (a bitwise
/// CRC-32, no table), joins the IDAT chunks, inflates them, and undoes filter 0. It reads 8-bit RGBA only.
/// </summary>
internal static class PngDecoder
{
    internal sealed record Chunk(string Type, byte[] Data);

    internal sealed record Image(int Width, int Height, byte[] Pixels, IReadOnlyList<Chunk> Chunks);

    internal static Image Decode(byte[] png)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal(signature, png[..8]);
        var chunks = new List<Chunk>();
        var at = 8;
        while (at < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png[(at + 8)..(at + 8 + length)];
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(BitwiseCrc(png.AsSpan(at + 4, 4 + length)), crc);
            chunks.Add(new Chunk(type, data));
            at += 12 + length;
        }

        var header = chunks[0];
        Assert.Equal("IHDR", header.Type);
        var width = BinaryPrimitives.ReadInt32BigEndian(header.Data);
        var height = BinaryPrimitives.ReadInt32BigEndian(header.Data.AsSpan(4));
        Assert.Equal([8, 6, 0, 0, 0], header.Data[8..]);

        using var joined = new MemoryStream();
        foreach (var chunk in chunks.Where(c => c.Type == "IDAT"))
        {
            joined.Write(chunk.Data);
        }

        joined.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(joined, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var stride = (width * 4) + 1;
        Assert.Equal(stride * height, raw.Length);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * stride]);
            raw.AsSpan((y * stride) + 1, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }

        return new Image(width, height, pixels, chunks);
    }

    private static uint BitwiseCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
