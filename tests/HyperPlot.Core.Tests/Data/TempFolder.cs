namespace HyperPlot.Tests;

/// <summary>A new, empty folder for the temporary file of one spatial tile index, removed with what is in it on <see cref="Dispose"/>.</summary>
internal sealed class TempFolder : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="TempFolder"/> class with a folder of its own.</summary>
    public TempFolder() => Path = Directory.CreateTempSubdirectory("hyperplot-test-").FullName;

    /// <summary>Gets the full path of the folder.</summary>
    public string Path { get; }

    /// <summary>Lists the temporary files of spatial tile indexes that have a name in the folder (<c>hyperplot-*.tmp</c>).</summary>
    /// <returns>The full paths of the files.</returns>
    public string[] CopyFiles() => Directory.GetFiles(Path, "hyperplot-*.tmp");

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
