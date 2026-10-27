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
}
