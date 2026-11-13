namespace HyperPlot;

/// <summary>
/// Where a frame draws the points of a snapshot (design §6.3 "描いた位置で測る", Issue #471 Decision 1): the pixels of a
/// difference from the snapshot's origins, and the pixel of a resident point as the vertex shader places it from its cell's
/// record (design §5.5.2 "GPU での写し方と精度"). <see cref="SpatialSnapshot.Nearest{TScreen}"/> measures distances in
/// these pixels, so that a point drawn on a unit of its cell is found where it is drawn.
/// </summary>
/// <remarks>
/// The along pixel follows x and the across pixel follows y, each along its own side of the panel, as a hit test reads
/// them (the screen's vertical for x when the plot is flipped). A struct that implements it is passed by reference, so
/// that a query makes no box and no call through an interface.
/// </remarks>
internal interface ISpatialScreen
{
    /// <summary>Returns the pixel along of a difference of x from the snapshot's origin, held where the axis clamps.</summary>
    /// <param name="x">The difference, in units of the scale space.</param>
    double Along(double x);

    /// <summary>Returns the pixel across of a difference of y from the snapshot's origin, held where the axis clamps.</summary>
    /// <param name="y">The difference, in units of the scale space.</param>
    double Across(double y);

    /// <summary>Takes the resident cell whose points the next calls of <see cref="Place"/> place: its record of this frame.</summary>
    /// <param name="cell">The cell.</param>
    void Enter(in ResidentCell cell);

    /// <summary>Returns the pixels of a point of the entered cell from its position in the cell, as the vertex shader draws it.</summary>
    /// <param name="unitX">The point's x in the cell, in units of 2^-32 of the cell's width.</param>
    /// <param name="unitY">The point's y in the cell, in units of 2^-32 of the cell's height.</param>
    (double Along, double Across) Place(uint unitX, uint unitY);
}
