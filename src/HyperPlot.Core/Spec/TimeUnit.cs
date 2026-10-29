namespace HyperPlot;

/// <summary>The unit of a long date-time column (design §3.1, §4.2).</summary>
public enum TimeUnit
{
    /// <summary>.NET ticks: 100 ns.</summary>
    Ticks,

    /// <summary>Nanoseconds.</summary>
    Nanoseconds,

    /// <summary>Microseconds.</summary>
    Microseconds,

    /// <summary>Milliseconds.</summary>
    Milliseconds,

    /// <summary>Seconds.</summary>
    Seconds,
}
