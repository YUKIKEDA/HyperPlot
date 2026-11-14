using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace HyperPlot;

/// <summary>
/// Throws the engine's exceptions from methods that are never inlined, so that the callers stay small (.cursor/rules/perf.mdc).
/// </summary>
internal static class ThrowHelper
{
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowChunkIndexOutOfRange(int chunkIndex, int chunkCount) =>
        throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, $"The column has {chunkCount} chunks.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowIndexOutOfRange(long index, long length) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"The column has {length} elements.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowRangeOutOfColumn(long start, long length, long columnLength) =>
        throw new ArgumentOutOfRangeException(
            nameof(start),
            $"The range [{start}, {start} + {length}) is not inside a column of {columnLength} elements."
        );

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowUnsupportedElementType(Type type) =>
        throw new NotSupportedException($"A column cannot hold {type.Name}; use float, double, int, long, or byte.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowColumnDisposed() => throw new ObjectDisposedException(null, "The column was disposed.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowIndexOutOfBlock(long index, long length) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"The block has {length} elements.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowRangeOutOfBlock(long first, long count, long length) =>
        throw new ArgumentOutOfRangeException(nameof(first), $"The range [{first}, {first} + {count}) is not inside a block of {length} elements.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowElementWidthMismatch(int size, int width) =>
        throw new ArgumentException($"The region's elements are {width} bytes wide, and the type asked for is {size} bytes wide.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialColumnsDiffer(long x, long y) =>
        throw new ArgumentException($"The x column has {x} rows and the y column has {y}: the columns of a spatial tile index are of one length.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialRowsInvalid(long start, long end) =>
        throw new ArgumentException($"The rows [{start}, {end}) of a spatial tile index start below 0 or after their end.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialRangeInvalid(string name, double lower, double upper) =>
        throw new ArgumentException($"{name} is [{lower}, {upper}]: an end is not finite, or the lower end is greater than the upper end.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialUnitInvalid(int rowsPerUnit) =>
        throw new ArgumentException($"A unit of {rowsPerUnit} rows is not a power of two of at most a chunk's rows.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialRectInvalid() =>
        throw new ArgumentException("An end of the rectangle is NaN, or a lower end is greater than its upper end.", "rect");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialLevelOutOfRange(int maxLevel) =>
        throw new ArgumentOutOfRangeException(nameof(maxLevel), maxLevel, "The deepest level to open is from 0 to 32.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowNearestRadiusOutOfRange(string name, double radius) =>
        throw new ArgumentOutOfRangeException(name, radius, "The radius is 0 or more pixels.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialSpansDiffer(int x, int y, int rows) =>
        throw new ArgumentException($"The spans to read points into are {x}, {y}, and {rows} elements long, and must be of one length.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialSnapshotNotFull() =>
        throw new InvalidOperationException("The snapshot has no copy to read points from: its stage is not Full.");

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowSpatialPointsOutOfRange(long first, long count, long pointCount) =>
        throw new ArgumentOutOfRangeException(
            nameof(first),
            $"The points [{first}, {first} + {count}) are not all among the snapshot's {pointCount} points."
        );

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowNoCoarseDensity(string paramName) =>
        throw new ArgumentException("The snapshot holds nothing, so it has no coarse density.", paramName);
}
