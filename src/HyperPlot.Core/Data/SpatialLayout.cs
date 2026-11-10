namespace HyperPlot;

/// <summary>
/// The numbers of a spatial tile index that follow from its number of axes D (design §3.4, §13): a code has ⌊64 / D⌋ bits
/// an axis, a cell has 2^D children, the first count is at level ⌊16 / D⌋, and one counting pass goes down at most
/// ⌊8 / D⌋ levels. D is 2 for the scatter index and 3 for the index of points in space (row K2).
/// </summary>
internal readonly struct SpatialLayout
{
    private SpatialLayout(int dimensions)
    {
        Dimensions = dimensions;
        BitsPerAxis = 64 / dimensions;
        TotalBits = dimensions * BitsPerAxis;
        FirstLevel = 16 / dimensions;
        MaxDescent = 8 / dimensions;
    }

    /// <summary>Gets the layout of two axes: 32 bits an axis, 4 children, the first count at level 8.</summary>
    public static SpatialLayout Plane => new(2);

    /// <summary>Gets the layout of three axes: 21 bits an axis, 8 children, the first count at level 5.</summary>
    public static SpatialLayout Space => new(3);

    /// <summary>Gets the number of axes, D.</summary>
    public int Dimensions { get; }

    /// <summary>Gets the bits of a code that one axis has. It is also the deepest level, whose cells are single grid cells.</summary>
    public int BitsPerAxis { get; }

    /// <summary>Gets the bits of a code that are used: 64 for two axes and 63 for three.</summary>
    public int TotalBits { get; }

    /// <summary>Gets the level of the first count.</summary>
    public int FirstLevel { get; }

    /// <summary>Gets the most levels that one counting pass goes down.</summary>
    public int MaxDescent { get; }

    /// <summary>Gets the number of children of a split cell.</summary>
    public int ChildCount => 1 << Dimensions;

    /// <summary>Gets the number of cells of the level of the first count.</summary>
    public int FirstCellCount => 1 << (Dimensions * FirstLevel);

    /// <summary>Returns the number, at a level, of the cell that holds a code: the code's upper D × level bits.</summary>
    /// <param name="code">The code of a point.</param>
    /// <param name="level">The level, from 0 to <see cref="BitsPerAxis"/>.</param>
    /// <returns>The cell's number.</returns>
    public ulong CellOf(ulong code, int level)
    {
        // A shift by 64 is a shift by 0 in C#, so level 0 of two axes is answered apart.
        return level == 0 ? 0 : code >> (TotalBits - (Dimensions * level));
    }

    /// <summary>Returns the number of a code's bits that lie below a level: the bits that tell the points of one cell of the level apart.</summary>
    /// <param name="level">The level, from 0 to <see cref="BitsPerAxis"/>.</param>
    /// <returns>The number of bits.</returns>
    public int BitsBelow(int level) => TotalBits - (Dimensions * level);
}
