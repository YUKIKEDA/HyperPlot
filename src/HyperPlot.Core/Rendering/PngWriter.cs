using System.Buffers.Binary;
using System.IO.Compression;

namespace HyperPlot.Rendering;

/// <summary>
/// Writes 8-bit RGBA pixels as a PNG (design §7.5): <c>IHDR</c>, <c>sRGB</c>, <c>gAMA</c>, <c>cHRM</c>, <c>IDAT</c> chunks of
/// at most 64 KiB, <c>IEND</c>. Rows use filter 0. The stream need not seek.
/// </summary>
internal static class PngWriter
{
    /// <summary>The size of the buffer that holds compressed data until it goes out as one <c>IDAT</c>.</summary>
    internal const int IdatSize = 64 * 1024;

    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Writes the image.</summary>
    /// <param name="stream">Where the PNG goes.</param>
    /// <param name="rgba">Width · Height · 4 bytes, top row first, alpha not premultiplied.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    internal static void Write(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        stream.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 6; // color type: RGBA
        header[10] = 0; // compression: deflate
        header[11] = 0; // filter method 0
        header[12] = 0; // no interlace
        WriteChunk(stream, "IHDR"u8, header);

        // The sRGB chunk, and the gAMA and cHRM values PNG §11.3.2.5 (Table 17) gives for it, for readers without sRGB.
        WriteChunk(stream, "sRGB"u8, [0]);
        Span<byte> gamma = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(gamma, 45455);
        WriteChunk(stream, "gAMA"u8, gamma);
        Span<byte> chromaticities = stackalloc byte[32];
        ReadOnlySpan<int> points = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
        for (var i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(chromaticities[(i * 4)..], points[i]);
        }

        WriteChunk(stream, "cHRM"u8, chromaticities);

        using (var idat = new IdatStream(stream))
        {
            using var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true);
            var row = new byte[1 + (width * 4)];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0; // filter: none
                rgba.Slice(y * width * 4, width * 4).CopyTo(row.AsSpan(1));
                zlib.Write(row);
            }
        }

        WriteChunk(stream, "IEND"u8, []);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, data.Length);
        stream.Write(field);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(field, Crc32.Of(type, data));
        stream.Write(field);
    }

    /// <summary>Takes the zlib stream and writes it out as <c>IDAT</c> chunks of <see cref="IdatSize"/> bytes, the last shorter.</summary>
    private sealed class IdatStream(Stream output) : Stream
    {
        private readonly byte[] _buffer = new byte[IdatSize];
        private int _count;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(buffer.Length, _buffer.Length - _count);
                buffer[..take].CopyTo(_buffer.AsSpan(_count));
                _count += take;
                buffer = buffer[take..];
                if (_count == _buffer.Length)
                {
                    Emit();
                }
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _count > 0)
            {
                Emit();
            }

            base.Dispose(disposing);
        }

        private void Emit()
        {
            WriteChunk(output, "IDAT"u8, _buffer.AsSpan(0, _count));
            _count = 0;
        }
    }
}

/// <summary>The CRC-32 of PNG (ISO 3309, polynomial 0xEDB88320), written here because <c>System.IO.Hashing</c> is not in the BCL.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = MakeTable();

    /// <summary>Returns the CRC of a chunk: its type, then its data.</summary>
    internal static uint Of(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data) => ~Update(Update(0xFFFFFFFFu, type), data);

    private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
