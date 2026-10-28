namespace HyperPlot.Rendering;

/// <summary>The depth texture a render target carries (design §5.2, §13.1).</summary>
public enum DepthFormat
{
    /// <summary>No depth texture. The only value v1 backends accept.</summary>
    None = 0,

    /// <summary>A 32-bit float depth texture, for the 3D rows after v1. v1 backends throw <see cref="NotSupportedException"/>.</summary>
    Float32 = 1,
}

/// <summary>What a render target is made from. After a device loss, the backend makes the target again from it (design §5.3).</summary>
/// <param name="Width">The width in physical pixels, from 1 to 16,384.</param>
/// <param name="Height">The height in physical pixels, from 1 to 16,384.</param>
/// <param name="Depth">The depth texture.</param>
public readonly record struct RenderTargetDescription(int Width, int Height, DepthFormat Depth = DepthFormat.None)
{
    /// <summary>The largest side of a render target: the largest side of a Direct3D 11 texture.</summary>
    public const int MaxSide = 16384;
}

/// <summary>
/// The fixed set of GPU operations a backend implements (design §5.2). A row adds what it implements and tests: row R1 has
/// render targets, clearing, and readback.
/// </summary>
/// <remarks>One thread at a time uses a backend and its render targets.</remarks>
public interface IGpuBackend : IDisposable
{
    /// <summary>Gets a value indicating whether the device is WARP, the software rasterizer.</summary>
    bool IsWarp { get; }

    /// <summary>Makes a render target. After a device loss, makes the device and its targets again first.</summary>
    /// <param name="description">The size and the depth texture.</param>
    /// <returns>The render target; dispose it before the backend.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A side is below 1 or above <see cref="RenderTargetDescription.MaxSide"/>.</exception>
    /// <exception cref="NotSupportedException"><see cref="RenderTargetDescription.Depth"/> is not <see cref="DepthFormat.None"/>.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or a render target could not be made again; the backend stays lost, and the next frame tries again.</exception>
    /// <exception cref="ObjectDisposedException">The backend was disposed.</exception>
    IRenderTarget CreateRenderTarget(in RenderTargetDescription description);

    /// <summary>
    /// Opens a frame and returns its scope, whose <see cref="FrameScope.Dispose"/> closes it (design §5.2): use
    /// <c>using var frame = backend.BeginFrame();</c>. Frames nest; the outermost one gets a number and its GPU time. After a
    /// device loss, makes the device and every render target again, cleared to transparent; what they held is gone.
    /// </summary>
    /// <returns>The scope of the frame.</returns>
    /// <exception cref="InvalidOperationException">After a device loss, the device or a render target could not be made again; the backend stays lost, and the next frame tries again.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while the render targets were made again.</exception>
    /// <exception cref="ObjectDisposedException">The backend was disposed.</exception>
    FrameScope BeginFrame();

    /// <summary>
    /// Draws a list's commands into a render target, in a frame of its own if none is open (design §5.3). Allocates
    /// nothing once the backend's buffers have grown to the list's size.
    /// </summary>
    /// <param name="target">A render target of this backend.</param>
    /// <param name="list">The commands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="list"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="target"/> belongs to another backend.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or a render target could not be made again; the backend stays lost, and the next frame tries again.</exception>
    /// <exception cref="ObjectDisposedException">The backend or the target was disposed.</exception>
    void Draw(IRenderTarget target, RenderList list);

    /// <summary>
    /// Closes the open frame of <paramref name="token"/> and the frames still open inside it; called by
    /// <see cref="FrameScope.Dispose"/>. A token of a frame already closed, or 0, does nothing: closing never throws for
    /// the order of the calls (design §5.2). Closing checks that the device is still there; while it is lost, closing
    /// only unwinds the nesting.
    /// </summary>
    /// <param name="token">The token <see cref="BeginFrame"/> gave the frame.</param>
    /// <exception cref="GpuDeviceLostException">The device was lost during the frame.</exception>
    void EndFrame(long token);

    /// <summary>
    /// Gets the GPU time of the newest outermost frame whose time is known, or null when none is yet (design §5.2). Times
    /// arrive some frames after their frame closes; a frame whose GPU clock was disjoint, or that saw a device loss, has
    /// none.
    /// </summary>
    GpuFrameTime? LastFrameTime { get; }
}

/// <summary>A texture a backend draws into (design §5.2).</summary>
public interface IRenderTarget : IDisposable
{
    /// <summary>Gets what the target was made from.</summary>
    RenderTargetDescription Description { get; }

    /// <summary>Fills the target with one color, in a frame of its own if none is open.</summary>
    /// <param name="color">The color, premultiplied by the target as design §5.2 writes.</param>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or a render target could not be made again; the backend stays lost, and the next frame tries again.</exception>
    /// <exception cref="ObjectDisposedException">The backend or the target was disposed.</exception>
    void Clear(Color color);

    /// <summary>
    /// Reads the pixels, top row first, as RGBA with the alpha taken out of the colors (design §5.2). Waits for the GPU.
    /// Allocates nothing after the first call.
    /// </summary>
    /// <param name="destination">At least Width · Height · 4 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than Width · Height · 4 bytes.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or a render target could not be made again; the backend stays lost, and the next frame tries again.</exception>
    /// <exception cref="ObjectDisposedException">The backend or the target was disposed.</exception>
    void ReadPixels(Span<byte> destination);
}

/// <summary>The GPU time of one outermost frame (design §5.2).</summary>
/// <param name="Frame">The frame's number, from 0 for the first outermost frame of the backend.</param>
/// <param name="Duration">The time the GPU took from the frame's start to its end.</param>
public readonly record struct GpuFrameTime(long Frame, TimeSpan Duration);

/// <summary>
/// The scope of an open frame (design §5.2). Disposing it closes the frame; disposing it again, or a default scope, does
/// nothing. Allocates nothing.
/// </summary>
public readonly struct FrameScope : IDisposable, IEquatable<FrameScope>
{
    private readonly IGpuBackend? _backend;
    private readonly long _token;

    /// <summary>Initializes a new instance of the <see cref="FrameScope"/> struct. Backends make it in <see cref="IGpuBackend.BeginFrame"/>.</summary>
    /// <param name="backend">The backend whose frame it is.</param>
    /// <param name="token">The token of the frame, above 0.</param>
    public FrameScope(IGpuBackend backend, long token)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(token);
        (_backend, _token) = (backend, token);
    }

    /// <summary>Gets the token of the frame; 0 for a default scope.</summary>
    public long Token => _token;

    /// <summary>Returns whether two scopes are of the same frame.</summary>
    /// <param name="left">One scope.</param>
    /// <param name="right">The other scope.</param>
    /// <returns>The answer.</returns>
    public static bool operator ==(FrameScope left, FrameScope right) => left.Equals(right);

    /// <summary>Returns whether two scopes are of different frames.</summary>
    /// <param name="left">One scope.</param>
    /// <param name="right">The other scope.</param>
    /// <returns>The answer.</returns>
    public static bool operator !=(FrameScope left, FrameScope right) => !left.Equals(right);

    /// <summary>Closes the frame, and the frames still open inside it, unless it is closed already.</summary>
    /// <exception cref="GpuDeviceLostException">The device was lost during the frame.</exception>
    public void Dispose() => _backend?.EndFrame(_token);

    /// <inheritdoc />
    public bool Equals(FrameScope other) => ReferenceEquals(_backend, other._backend) && _token == other._token;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FrameScope other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_backend, _token);
}
