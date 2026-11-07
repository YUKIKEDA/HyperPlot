namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// A resource made on a backend's device (design §5.3): a render target or a host's surface. A device loss releases it, and
/// the next frame makes it again with the device; disposing it removes it from its backend.
/// </summary>
internal interface IDeviceResource : IDisposable
{
    /// <summary>Makes the resource's objects on the backend's device.</summary>
    void MakeResources();

    /// <summary>Releases the resource's objects; what it needs to make them again stays.</summary>
    void ReleaseResources();
}
