namespace HyperPlot;

/// <summary>
/// The element types a column can hold (design §3.1). The checks are constants for each instantiation, so the JIT and
/// NativeAOT fold them away.
/// </summary>
internal static class ColumnElement<T>
    where T : unmanaged
{
    internal static bool IsSupported =>
        typeof(T) == typeof(float)
        || typeof(T) == typeof(double)
        || typeof(T) == typeof(int)
        || typeof(T) == typeof(long)
        || typeof(T) == typeof(byte);

    internal static void Validate()
    {
        if (!IsSupported)
        {
            ThrowHelper.ThrowUnsupportedElementType(typeof(T));
        }
    }
}
