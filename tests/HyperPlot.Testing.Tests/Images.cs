using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HyperPlot.Testing.Tests;

/// <summary>Images and PNG files made by hand for the tests of the comparison and the decoder.</summary>
internal static class Images
{
    /// <summary>Returns a <paramref name="width"/> × <paramref name="height"/> RGBA image of one color.</summary>
    internal static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    /// <summary>Returns an RGBA image whose pixels all differ from each other, so that a misplaced pixel shows.</summary>
    internal static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)(x * 7);
                pixels[i + 1] = (byte)(y * 11);
                pixels[i + 2] = (byte)((x + y) * 3);
                pixels[i + 3] = (byte)(200 + x);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Builds a PNG from its header fields and raw rows (each row with its filter byte), with its own CRC, so that a test
    /// can make the forms the decoder must reject. <paramref name="idat"/>, when given, turns the compressed data into what
    /// the IDAT chunk holds, or into null to leave the chunk out.
    /// </summary>
    internal static byte[] Png(int width, int height, byte bitDepth, byte colorType, byte interlace, byte[] rows, Func<byte[], byte[]?>? idat = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colorType;
        header[12] = interlace;
        Chunk(stream, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(rows);
            }

            var data = idat is null ? compressed.ToArray() : idat(compressed.ToArray());
            if (data is not null)
            {
                Chunk(stream, "IDAT", data);
            }
        }

        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    /// <summary>Returns the raw rows of an RGBA image, each with filter byte <paramref name="filter"/>.</summary>
    internal static byte[] Rows(byte[] rgba, int width, int height, byte filter)
    {
        var rows = new byte[((width * 4) + 1) * height];
        for (var y = 0; y < height; y++)
        {
            rows[y * ((width * 4) + 1)] = filter;
            rgba.AsSpan(y * width * 4, width * 4).CopyTo(rows.AsSpan((y * ((width * 4) + 1)) + 1));
        }

        return rows;
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc(byte[] bytes)
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
