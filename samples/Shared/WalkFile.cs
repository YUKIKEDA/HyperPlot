using System.Runtime.InteropServices;

namespace HyperPlot.Samples;

/// <summary>
/// The data files of the 1e9-point samples and benchmarks: series <c>i</c> is <c>series{i}.f32</c>, a random walk of
/// floats from seed <c>0x48797065 + i</c>, so that every program that uses them draws the same data.
/// </summary>
internal static class WalkFile
{
    /// <summary>Returns the path of series <paramref name="index"/> in <paramref name="directory"/>, writing the file first if it is missing or of another length.</summary>
    /// <returns>Whether the file was written.</returns>
    public static bool Ensure(string directory, int index, long points, out string path)
    {
        path = Path.Combine(directory, $"series{index}.f32");
        if (File.Exists(path) && new FileInfo(path).Length == points * sizeof(float))
        {
            return false;
        }

        Write(path, points, seed: 0x48797065UL + (ulong)index);
        return true;
    }

    // A seeded random walk of floats (xorshift64*), written in blocks of 4 MiB.
    private static void Write(string path, long count, ulong seed)
    {
        var state = seed;
        var y = 0.0;
        var block = new float[1 << 20];
        using var stream = File.Create(path);
        for (long written = 0; written < count; written += block.Length)
        {
            var length = (int)Math.Min(block.Length, count - written);
            for (var i = 0; i < length; i++)
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                y += (((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53))) - 0.5;
                block[i] = (float)y;
            }

            stream.Write(MemoryMarshal.AsBytes(block.AsSpan(0, length)));
        }
    }
}
