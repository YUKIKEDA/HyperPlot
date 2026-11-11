using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// One 32-byte row of the table of a spatial tile index (design §3.4 "表"): a cell, the range of its points in the
/// copy, and where its children are. The children of a split cell are next to each other in the order of their codes,
/// the empty ones too.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal struct SpatialRow
{
    /// <summary>The cell's number at its level: the upper D × <see cref="Level"/> bits of its points' codes.</summary>
    public ulong Code;

    /// <summary>The position of the cell's first point in the copy.</summary>
    public long First;

    /// <summary>The number of the cell's points.</summary>
    public long Count;

    /// <summary>The row of the first child of a split cell, and −1 in a leaf.</summary>
    public int FirstChild;

    /// <summary>The cell's level.</summary>
    public byte Level;

    /// <summary>The cell's <see cref="SpatialCellKind"/>.</summary>
    public byte Kind;

    /// <summary>
    /// The 2 bytes that fill the row, always 0. They are a field so that a row has no byte the runtime may leave
    /// unwritten: the same input gives the same bytes of the table.
    /// </summary>
    public ushort Reserved;
}
