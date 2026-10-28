using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

/// <summary>A file of raw values in the temporary folder, deleted on dispose.</summary>
internal sealed class TempFile : IDisposable
{
    private TempFile(string path) => Path = path;

    public string Path { get; }

    /// <summary>Writes <paramref name="prefixBytes"/> bytes of 0xAB, then the values' bytes.</summary>
    public static TempFile Write<T>(ReadOnlySpan<T> values, int prefixBytes)
        where T : unmanaged
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hyperplot-{Guid.NewGuid():N}.bin");
        using (var stream = File.Create(path))
        {
            for (var i = 0; i < prefixBytes; i++)
            {
                stream.WriteByte(0xAB);
            }

            stream.Write(MemoryMarshal.AsBytes(values));
        }

        return new TempFile(path);
    }

    public void Dispose() => File.Delete(Path);
}
