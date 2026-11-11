namespace HyperPlot;

/// <summary>
/// Makes the copy of a spatial tile index (design §3.4 "置き場所"). The build calls it once, from a worker, and only
/// when the index reads at least one row. The tests replace it to count the calls, to make an allocation throw, and to
/// stand for an operating system without a temporary file.
/// </summary>
internal class SpatialCopyAllocator
{
    /// <summary>How an operating system keeps the temporary file of a copy from outliving the process.</summary>
    internal enum TempFileKind
    {
        /// <summary>The operating system has no temporary file for a copy yet.</summary>
        None,

        /// <summary>Windows: the file is deleted when its last handle closes.</summary>
        DeleteOnClose,

        /// <summary>Linux: the file's name is removed as soon as the file is open.</summary>
        Unlinked,

        /// <summary>
        /// Linux, on a file system that cannot reserve a file in one call: as <see cref="Unlinked"/>, with the blocks
        /// reserved piece by piece. The engine finds such a file system by itself; the tests ask for it.
        /// </summary>
        UnlinkedInPieces,
    }

    /// <summary>Gets the allocator that the engine uses.</summary>
    public static SpatialCopyAllocator Default { get; } = new();

    /// <summary>Gets how this operating system keeps the temporary file from outliving the process.</summary>
    public virtual TempFileKind TempFile =>
        OperatingSystem.IsWindows() ? TempFileKind.DeleteOnClose
        : OperatingSystem.IsLinux() ? TempFileKind.Unlinked
        : TempFileKind.None;

    /// <summary>Makes a copy.</summary>
    /// <param name="capacity">The number of rows the index reads: positive.</param>
    /// <param name="valueWidths">The bytes of one stored value of each axis.</param>
    /// <param name="mapped">Whether a column of the index is memory-mapped, so that the copy goes to a temporary file.</param>
    /// <param name="tempFolder">The folder of the temporary file, or <see langword="null"/> for <see cref="Path.GetTempPath"/>.</param>
    /// <param name="stopped">
    /// Returns whether the build that asks was stopped, or <see langword="null"/> when nothing stops it. An allocation
    /// that takes long looks at it and throws <see cref="OperationCanceledException"/> (design §3.4 "一時ファイル").
    /// </param>
    /// <returns>The copy. Its contents are not set.</returns>
    public virtual SpatialCopy Allocate(long capacity, ReadOnlySpan<int> valueWidths, bool mapped, string? tempFolder, Func<bool>? stopped = null) =>
        mapped
            ? SpatialCopy.Map(capacity, valueWidths, tempFolder ?? Path.GetTempPath(), TempFile, stopped)
            : SpatialCopy.Allocate(capacity, valueWidths);
}
