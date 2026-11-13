using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// A point of a spatial tile index as the GPU holds it (design §5.5.2 "常駐のセルとページ"): its position in its resident
/// cell on each axis, in units of 2^-32 of the cell's extent, and the cell's number. 12 bytes, laid out as the HLSL
/// structure of Markers.hlsl.
/// </summary>
/// <param name="X">The position in the cell along x.</param>
/// <param name="Y">The position in the cell along y.</param>
/// <param name="Cell">The index of the cell in <see cref="SpatialSnapshot.ResidentCells"/>.</param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ResidentPoint(uint X, uint Y, uint Cell);
