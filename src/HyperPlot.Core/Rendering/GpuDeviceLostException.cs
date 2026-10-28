namespace HyperPlot.Rendering;

/// <summary>
/// The GPU device was lost (design §5.3). The next frame makes the device and the render targets again; what the targets
/// held is gone, so draw again.
/// </summary>
public sealed class GpuDeviceLostException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="GpuDeviceLostException"/> class.</summary>
    public GpuDeviceLostException() { }

    /// <summary>Initializes a new instance of the <see cref="GpuDeviceLostException"/> class.</summary>
    /// <param name="message">The message.</param>
    public GpuDeviceLostException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="GpuDeviceLostException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public GpuDeviceLostException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Initializes a new instance of the <see cref="GpuDeviceLostException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="result">The HRESULT of the call that saw the loss.</param>
    /// <param name="removedReason">The HRESULT the device gave as the reason.</param>
    public GpuDeviceLostException(string message, int result, int removedReason)
        : base(message)
    {
        HResult = result;
        RemovedReason = removedReason;
    }

    /// <summary>
    /// Gets the HRESULT the device gave as the reason it was removed, or the HRESULT of the call that saw the loss when the
    /// device gave none yet.
    /// </summary>
    public int RemovedReason { get; }
}
