namespace HyperPlot;

/// <summary>
/// Sorts short spans without allocating: the frame path's (design §11 invariant 3, Issue #570) and a worker's that runs
/// once per x (the stack's crossings, Issue #585).
/// </summary>
/// <remarks>
/// A sort with no comparison allocates in a NativeAOT build and not under the JIT, so the Zero-Alloc tests do not see it.
/// Measured in a scratch program on .NET 10.0.14, 1,000 calls each after a collection: <c>Span&lt;float&gt;.Sort()</c> of 2
/// values, <c>Span&lt;double&gt;.Sort()</c> of 2 and of 4, and <c>List&lt;double&gt;.Sort()</c> of 3 allocated 48 bytes a
/// call under NativeAOT and 0 under the JIT; the same sorts with a static comparison allocated 0 in both.
/// </remarks>
internal static class SpanSort
{
    /// <summary>Sorts values in ascending order, NaN first, as <see cref="float.CompareTo(float)"/> orders them.</summary>
    /// <param name="values">The values to sort in place.</param>
    public static void Ascending(Span<float> values) => values.Sort(static (a, b) => a.CompareTo(b));

    /// <summary>Sorts values in ascending order, NaN first, as <see cref="double.CompareTo(double)"/> orders them.</summary>
    /// <param name="values">The values to sort in place.</param>
    public static void Ascending(Span<double> values) => values.Sort(static (a, b) => a.CompareTo(b));
}
