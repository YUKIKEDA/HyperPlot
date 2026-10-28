using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.DataPath;

/// <summary>
/// The seeded series file of design §9: raw little-endian <see cref="float"/> values of a random walk, written unbuffered so
/// that the file is not in the page cache after the write.
/// </summary>
internal static class SeriesFile
{
    /// <summary>The default seed; a figure records the seed it used.</summary>
    internal const ulong DefaultSeed = 0x48_79_70_65_72_50_6C_6FUL;

    private const int BlockBytes = 4 << 20;
    private const int Sector = 4096;

    // FILE_FLAG_NO_BUFFERING: the write goes past the page cache. The B1 prototype found the file cold after it, even when
    // it was cached before (Issue #26); each cold scene checks it again against its warm repeat.
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Writes <paramref name="points"/> values from <paramref name="seed"/>; the same seed gives the same bytes.</summary>
    internal static unsafe void Write(string path, long points, ulong seed)
    {
        var bytes = points * sizeof(float);
        var buffer = (float*)NativeMemory.AlignedAlloc(BlockBytes, Sector);
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, NoBuffering | FileOptions.WriteThrough);
            var state = seed;
            var y = 0.0;
            long written = 0;
            while (written < bytes)
            {
                var block = new Span<float>(buffer, BlockBytes / sizeof(float));
                for (var i = 0; i < block.Length; i++)
                {
                    y += ((Next(ref state) >> 11) * (1.0 / (1UL << 53))) - 0.5;
                    block[i] = (float)y;
                }

                // An unbuffered write is a whole number of sectors; the last one is padded and cut off below.
                var length = (int)Math.Min(BlockBytes, (bytes - written + Sector - 1) & -Sector);
                RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, length), written);
                written += length;
            }

            RandomAccess.SetLength(handle, bytes);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    /// <summary>Returns the SHA-256 of the file in hex. Reading it puts the file into the page cache.</summary>
    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>xorshift64*, the same generator as the kernel benchmarks.</summary>
    private static ulong Next(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state * 0x2545F4914F6CDD1DUL;
    }
}
