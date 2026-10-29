using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HyperPlot.Testing;

/// <summary>
/// Reads the PNGs that <see cref="Rendering.HeadlessRenderer.WritePng"/> writes (design §7.5, §10.1): 8-bit RGBA, color type
/// 6, no interlace, row filter 0. It is written apart from Core's writer, CRC-32 included (bitwise, no table), so that the
/// PNG tests of Core and D3D11 read the writer's output with code of their own. Any other form, and a broken file, throws
/// <see cref="InvalidDataException"/> with the reason.
/// </summary>
internal static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes a PNG.</summary>
    /// <param name="png">The bytes of the file.</param>
    /// <returns>The image: its size, its RGBA pixels, and its chunks in file order.</returns>
    /// <exception cref="InvalidDataException">The file is not a PNG of the form above, or is broken.</exception>
    internal static Image Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("The file does not start with the PNG signature.");
        }

        var chunks = new List<Chunk>();
        var at = Signature.Length;
        while (at < png.Length)
        {
            if (png.Length - at < 12)
            {
                throw new InvalidDataException($"The chunk at byte {at} is cut off: {png.Length - at} bytes are left of its 12-byte frame.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(png[at..]);
            if (length < 0 || length > png.Length - at - 12)
            {
                throw new InvalidDataException($"The chunk at byte {at} says it holds {length} bytes; the file has {png.Length - at - 12} left.");
            }

            var type = Encoding.ASCII.GetString(png.Slice(at + 4, 4));
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[(at + 8 + length)..]);
            if (BitwiseCrc(png.Slice(at + 4, 4 + length)) != crc)
            {
                throw new InvalidDataException($"The CRC of the {type} chunk at byte {at} does not match its bytes.");
            }

            chunks.Add(new Chunk(type, png.Slice(at + 8, length).ToArray()));
            at += 12 + length;
        }

        if (chunks.Count == 0 || chunks[0].Type != "IHDR" || chunks[0].Data.Length != 13)
        {
            throw new InvalidDataException("The first chunk is not a 13-byte IHDR.");
        }

        var header = chunks[0].Data;
        var width = BinaryPrimitives.ReadInt32BigEndian(header);
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        if (header[8] != 8 || header[9] != 6 || header[10] != 0 || header[11] != 0 || header[12] != 0)
        {
            throw new InvalidDataException(
                $"The image has bit depth {header[8]}, color type {header[9]}, compression {header[10]}, filter method {header[11]}, "
                    + $"and interlace {header[12]}; only 8-bit RGBA (8, 6, 0, 0, 0) is read."
            );
        }

        var pixelBytes = (long)width * height * 4;
        if (width < 1 || height < 1 || pixelBytes > Array.MaxLength)
        {
            throw new InvalidDataException($"The image is {width} × {height}; it must be at least 1 × 1 and fit in one array.");
        }

        var raw = Inflate(chunks);
        var stride = (width * 4) + 1;
        if (raw.Length != (long)stride * height)
        {
            throw new InvalidDataException($"The image data inflates to {raw.Length} bytes; {width} × {height} needs {(long)stride * height}.");
        }

        var pixels = new byte[pixelBytes];
        for (var y = 0; y < height; y++)
        {
            if (raw[y * stride] != 0)
            {
                throw new InvalidDataException($"Row {y} uses filter {raw[y * stride]}; only filter 0 (none) is read.");
            }

            raw.AsSpan((y * stride) + 1, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        }

        return new Image(width, height, pixels, chunks);
    }

    private static byte[] Inflate(List<Chunk> chunks)
    {
        using var joined = new MemoryStream();
        foreach (var chunk in chunks)
        {
            if (chunk.Type == "IDAT")
            {
                joined.Write(chunk.Data);
            }
        }

        if (joined.Length == 0)
        {
            throw new InvalidDataException("The file has no IDAT chunk.");
        }

        joined.Position = 0;
        using var inflated = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(joined, CompressionMode.Decompress);
            zlib.CopyTo(inflated);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"The image data does not inflate: {exception.Message}", exception);
        }

        return inflated.ToArray();
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

    /// <summary>A chunk of the file.</summary>
    /// <param name="Type">The 4-letter type.</param>
    /// <param name="Data">The data, without the length, type, and CRC.</param>
    internal sealed record Chunk(string Type, byte[] Data);

    /// <summary>A decoded image.</summary>
    /// <param name="Width">The width in pixels.</param>
    /// <param name="Height">The height in pixels.</param>
    /// <param name="Pixels">The pixels: RGBA, 4 bytes each, rows top to bottom with no padding.</param>
    /// <param name="Chunks">The chunks in file order.</param>
    internal sealed record Image(int Width, int Height, byte[] Pixels, IReadOnlyList<Chunk> Chunks);
}
