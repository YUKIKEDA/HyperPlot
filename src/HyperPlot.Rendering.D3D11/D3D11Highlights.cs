using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The regions of the selections a list's highlights keep, on the GPU (design §6.4 "生の点の強調", "密度の強調", row R3c): a
/// dynamic structured buffer of the regions and one of the lassos' mask words, which the resident markers' vertex shader and
/// the images' pixel shader read. Sent with each list that has regions; made at the first, and again after a loss.
/// </summary>
internal sealed unsafe class D3D11Highlights
{
    private readonly D3D11Backend _backend;
    private DynamicBuffer _regions;
    private DynamicBuffer _masks;

    internal D3D11Highlights(D3D11Backend backend) => _backend = backend;

    /// <summary>Gets the view of the regions, or null before a list had one.</summary>
    internal ID3D11ShaderResourceView* Regions => _regions.View;

    /// <summary>Gets the view of the masks' words, or null before a list had one.</summary>
    internal ID3D11ShaderResourceView* Masks => _masks.View;

    /// <summary>Sends a list's regions and masks: nothing for a list of none, whose records name none.</summary>
    internal void Upload(ReadOnlySpan<HighlightRegion> regions, ReadOnlySpan<uint> masks)
    {
        if (regions.IsEmpty)
        {
            return;
        }

        Send(ref _regions, regions);

        // A view of a buffer holds an element at least: a list of rectangles alone sends a word.
        Send(ref _masks, masks.IsEmpty ? [0u] : masks);
    }

    internal void Release()
    {
        _regions.Release();
        _masks.Release();
    }

    private void Send<T>(ref DynamicBuffer buffer, ReadOnlySpan<T> items)
        where T : unmanaged
    {
        if (items.Length > buffer.Capacity)
        {
            var capacity = Math.Max(items.Length, Math.Max(16, buffer.Capacity * 2));
            buffer.Release();
            var description = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)(capacity * sizeof(T)),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = (uint)sizeof(T),
            };
            ID3D11Buffer* made;
            _backend.Check(_backend.Device->CreateBuffer(&description, null, &made), "CreateBuffer");
            buffer.Data = made;
            var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
            };
            view.Buffer.FirstElement = 0;
            view.Buffer.NumElements = (uint)capacity;
            ID3D11ShaderResourceView* resourceView;
            _backend.Check(_backend.Device->CreateShaderResourceView((ID3D11Resource*)made, &view, &resourceView), "CreateShaderResourceView");
            buffer.View = resourceView;
            buffer.Capacity = capacity;
        }

        // The mapped memory holds the capacity; the span bounds the copy to the items there are.
        D3D11_MAPPED_SUBRESOURCE mapped;
        var context = _backend.Context;
        _backend.Check(context->Map((ID3D11Resource*)buffer.Data, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        items.CopyTo(new Span<T>(mapped.pData, buffer.Capacity));
        context->Unmap((ID3D11Resource*)buffer.Data, 0);
    }

    /// <summary>A dynamic structured buffer, its view, and the elements it holds.</summary>
    private struct DynamicBuffer
    {
        public ID3D11Buffer* Data;
        public ID3D11ShaderResourceView* View;
        public int Capacity;

        public void Release()
        {
            if (View != null)
            {
                _ = View->Release();
                View = null;
            }

            if (Data != null)
            {
                _ = Data->Release();
                Data = null;
            }

            Capacity = 0;
        }
    }
}
