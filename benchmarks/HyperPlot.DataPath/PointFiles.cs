using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.DataPath;

/// <summary>
/// The seeded x and y files of the spatial tile index scenes (design §9, row C3a): raw little-endian <see cref="float"/>
/// values, written unbuffered so that the files are not in the page cache after the write. Each block of points has a
/// generator of its own, seeded from the seed and the block's number, so the blocks are made in parallel and the same
/// seed gives the same bytes.
/// </summary>
internal static class PointFiles
{
    /// <summary>The side of the square that the uniform points and the centers of the clusters fill, in data units.</summary>
    internal const double Side = 1e6;

    /// <summary>The number of clusters of the clustered distribution.</summary>
    internal const int Clusters = 32;

    /// <summary>The share of the clustered points that are outliers.</summary>
    internal const double OutlierShare = 0.001;

    /// <summary>The half side of the square that the outliers fill, in data units.</summary>
    internal const double OutlierReach = 1e9;

    private const int BlockPoints = 1 << 20;
    private const int BlocksPerBatch = 16;
    private const int Sector = 4096;

    // FILE_FLAG_NO_BUFFERING, as in SeriesFile.
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Returns the path of the x file and of the y file for a path prefix.</summary>
    internal static (string X, string Y) Paths(string prefix) => (prefix + ".x", prefix + ".y");

    /// <summary>
    /// Writes <paramref name="points"/> pairs of the distribution from <paramref name="seed"/>.
    /// <list type="bullet">
    /// <item><description>"uniform": x and y uniform over [0, <see cref="Side"/>).</description></item>
    /// <item><description>
    /// "clustered": <see cref="Clusters"/> Gaussian clusters whose centers are uniform over [0, <see cref="Side"/>)², whose
    /// standard deviations are 10 × 3000^u (10 to 30,000), and whose weights are 1 / (k + 1) for the cluster k; and a
    /// share <see cref="OutlierShare"/> of outliers uniform over (−<see cref="OutlierReach"/>, <see cref="OutlierReach"/>)².
    /// </description></item>
    /// </list>
    /// </summary>
    internal static unsafe void Write(string prefix, long points, ulong seed, string distribution)
    {
        var clusters = distribution switch
        {
            "uniform" => null,
            "clustered" => MakeClusters(seed),
            _ => throw new ArgumentException($"Unknown distribution '{distribution}': write uniform or clustered."),
        };
        var (xPath, yPath) = Paths(prefix);
        var batchBytes = (long)BlockPoints * BlocksPerBatch * sizeof(float);
        var xs = (float*)NativeMemory.AlignedAlloc((nuint)batchBytes, Sector);
        var ys = (float*)NativeMemory.AlignedAlloc((nuint)batchBytes, Sector);
        try
        {
            using var xHandle = File.OpenHandle(xPath, FileMode.Create, FileAccess.Write, FileShare.None, NoBuffering | FileOptions.WriteThrough);
            using var yHandle = File.OpenHandle(yPath, FileMode.Create, FileAccess.Write, FileShare.None, NoBuffering | FileOptions.WriteThrough);
            var blocks = (points + BlockPoints - 1) / BlockPoints;
            for (long first = 0; first < blocks; first += BlocksPerBatch)
            {
                var count = (int)Math.Min(BlocksPerBatch, blocks - first);
                var batchStart = first;
                Parallel.For(
                    0,
                    count,
                    b =>
                    {
                        var block = batchStart + b;
                        var x = new Span<float>(xs + ((long)b * BlockPoints), BlockPoints);
                        var y = new Span<float>(ys + ((long)b * BlockPoints), BlockPoints);
                        Fill(block, seed, clusters, x, y);
                    }
                );

                // An unbuffered write is a whole number of sectors; the last one is padded and cut off below.
                var offset = first * BlockPoints * sizeof(float);
                var length = (int)Math.Min((long)count * BlockPoints * sizeof(float), ((points * sizeof(float)) - offset + Sector - 1) & -Sector);
                RandomAccess.Write(xHandle, new ReadOnlySpan<byte>(xs, length), offset);
                RandomAccess.Write(yHandle, new ReadOnlySpan<byte>(ys, length), offset);
            }

            RandomAccess.SetLength(xHandle, points * sizeof(float));
            RandomAccess.SetLength(yHandle, points * sizeof(float));
        }
        finally
        {
            NativeMemory.AlignedFree(xs);
            NativeMemory.AlignedFree(ys);
        }
    }

    /// <summary>Returns the SHA-256 of the x file and of the y file in hex. Reading them puts them into the page cache.</summary>
    internal static (string X, string Y) Hash(string prefix)
    {
        var (xPath, yPath) = Paths(prefix);
        return (HashOf(xPath), HashOf(yPath));

        static string HashOf(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }

    private static void Fill(long block, ulong seed, Cluster[]? clusters, Span<float> x, Span<float> y)
    {
        var state = SplitMix(seed ^ SplitMix((ulong)block + 1));
        if (clusters is null)
        {
            for (var i = 0; i < x.Length; i++)
            {
                x[i] = (float)(Unit(ref state) * Side);
                y[i] = (float)(Unit(ref state) * Side);
            }

            return;
        }

        for (var i = 0; i < x.Length; i++)
        {
            var pick = Unit(ref state);
            if (pick < OutlierShare)
            {
                x[i] = (float)(((2 * Unit(ref state)) - 1) * OutlierReach);
                y[i] = (float)(((2 * Unit(ref state)) - 1) * OutlierReach);
                continue;
            }

            // The pick, rescaled past the outliers, chooses the cluster by its cumulative weight.
            var share = (pick - OutlierShare) / (1 - OutlierShare);
            var k = 0;
            while (k < clusters.Length - 1 && share >= clusters[k].Cumulative)
            {
                k++;
            }

            // Box-Muller: two uniform values make two independent standard normal values.
            var radius = Math.Sqrt(-2 * Math.Log(1 - Unit(ref state)));
            var angle = 2 * Math.PI * Unit(ref state);
            var cluster = clusters[k];
            x[i] = (float)(cluster.X + (cluster.Sigma * radius * Math.Cos(angle)));
            y[i] = (float)(cluster.Y + (cluster.Sigma * radius * Math.Sin(angle)));
        }
    }

    private static Cluster[] MakeClusters(ulong seed)
    {
        var state = SplitMix(seed);
        var clusters = new Cluster[Clusters];
        var total = 0.0;
        for (var k = 0; k < Clusters; k++)
        {
            total += 1.0 / (k + 1);
        }

        var cumulative = 0.0;
        for (var k = 0; k < Clusters; k++)
        {
            cumulative += 1.0 / (k + 1) / total;
            clusters[k] = new Cluster(Unit(ref state) * Side, Unit(ref state) * Side, 10 * Math.Pow(3000, Unit(ref state)), cumulative);
        }

        return clusters;
    }

    /// <summary>A uniform value in [0, 1) from xorshift64*, the generator of <see cref="SeriesFile"/>.</summary>
    private static double Unit(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return ((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>SplitMix64, which turns a seed and a block's number into a state that xorshift64* can start from (never 0).</summary>
    private static ulong SplitMix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value == 0 ? 1 : value;
    }

    private readonly record struct Cluster(double X, double Y, double Sigma, double Cumulative);
}
