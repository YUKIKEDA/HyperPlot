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
    /// Starts a frame. After a device loss, makes the device and every render target again, cleared to transparent; what
    /// they held is gone.
    /// </summary>
    /// <exception cref="InvalidOperationException">After a device loss, the device or a render target could not be made again; the backend stays lost, and the next frame tries again.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while the render targets were made again.</exception>
    /// <exception cref="ObjectDisposedException">The backend was disposed.</exception>
    void BeginFrame();

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

    /// <summary>Ends a frame, and checks that the device is still there. Does nothing while the device is lost.</summary>
    /// <exception cref="GpuDeviceLostException">The device was lost during the frame.</exception>
    void EndFrame();
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
