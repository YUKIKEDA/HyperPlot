namespace HyperPlot;

/// <summary>
/// The identity of a spec node, unique in the process (design §4.1). A node gets a new one when it is made and each time
/// <c>with</c> copies it; 0 is never given.
/// </summary>
/// <param name="Value">The number.</param>
public readonly record struct SpecId(long Value);
