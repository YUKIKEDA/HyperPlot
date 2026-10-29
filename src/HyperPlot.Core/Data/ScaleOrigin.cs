namespace HyperPlot;

/// <summary>
/// The origins of x and y that M4 answers in (design §3.6, §5.5.1): boundaries are given and points are written as
/// differences from them, so that values far from 0 (date-time ticks, nanoseconds since 1970) keep their precision.
/// </summary>
/// <param name="X">The origin of x, an integer in the X column's unit.</param>
/// <param name="Y">The origin of y, an integer in the Y column's unit.</param>
public readonly record struct ScaleOrigin(long X, long Y);
