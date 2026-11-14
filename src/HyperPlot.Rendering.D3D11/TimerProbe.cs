namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// Spike R3b2 (Issue #472): what <see cref="D3D11FrameTimer"/> did in each outermost frame, so that a scene can tell a
/// frame that found the ring full from one whose time arrived in the same read as a newer one. Preallocated; not for merging.
/// </summary>
public static class TimerProbe
{
    /// <summary>The frames kept (the newest overwrite the oldest).</summary>
    public const int Capacity = 4096;

    /// <summary>Gets, per frame number mod <see cref="Capacity"/>, whether the frame got a query set (1) or found the ring full (0).</summary>
    public static readonly byte[] Timed = new byte[Capacity];

    /// <summary>Gets, per frame number, how many sets the frame's closing read.</summary>
    public static readonly byte[] ReadAtEnd = new byte[Capacity];

    /// <summary>Gets, per frame number, the sets still pending after the frame's closing read.</summary>
    public static readonly byte[] PendingAfterEnd = new byte[Capacity];

    /// <summary>Gets, per frame number, the frame whose set the closing read stopped at (not ready), or -1.</summary>
    public static readonly long[] StoppedAt = new long[Capacity];

    /// <summary>Gets, per frame number, the frame number of the closing that read this frame's set, or -1.</summary>
    public static readonly long[] ReadBy = new long[Capacity];

    /// <summary>Gets, per frame number, the GPU timestamp at the frame's start and end, and the clock's frequency.</summary>
    public static readonly ulong[] GpuStart = new ulong[Capacity];

    /// <summary>Gets, per frame number, the GPU timestamp at the frame's end.</summary>
    public static readonly ulong[] GpuEnd = new ulong[Capacity];

    /// <summary>Gets, per frame number, the CPU time (ticks of TimeProvider.System, the Stopwatch clock) right after the frame's closing flushed its commands.</summary>
    public static readonly long[] SubmitQpc = new long[Capacity];

    /// <summary>Gets, per frame number, the CPU time at which a probe between frames first saw the frame's set ready (0: never seen).</summary>
    public static readonly long[] ReadyQpc = new long[Capacity];

    /// <summary>HP_PROBE_DRAIN=1: the frame's end timestamp follows a copy of one pixel of its target.</summary>
    public static readonly bool Drain = Environment.GetEnvironmentVariable("HP_PROBE_DRAIN") == "1";

    /// <summary>HP_PROBE_READY_FLUSH=1: the readiness probe between frames reads with a flush.</summary>
    public static readonly bool ReadyFlush = Environment.GetEnvironmentVariable("HP_PROBE_READY_FLUSH") == "1";

    /// <summary>Gets the GPU clock's frequency of the newest read set.</summary>
    public static ulong Frequency { get; internal set; }

    /// <summary>Gets the number of the outermost frame that closed last.</summary>
    public static long Closing { get; internal set; } = -1;

    internal static void Clear(long frame)
    {
        var i = (int)(frame % Capacity);
        Timed[i] = 0;
        ReadAtEnd[i] = 0;
        PendingAfterEnd[i] = 0;
        StoppedAt[i] = -1;
        ReadBy[i] = -1;
        GpuStart[i] = 0;
        GpuEnd[i] = 0;
        SubmitQpc[i] = 0;
        ReadyQpc[i] = 0;
    }
}
