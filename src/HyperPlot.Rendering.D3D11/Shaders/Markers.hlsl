// Point markers (design §5.1, row R3a). One quad per marker: MarkerVS makes it from SV_VertexID over the marker's box
// widened outward to whole pixels, and MarkerPS covers each pixel by the marker's shape and multiplies the premultiplied
// color by the coverage. Positions and sizes are physical pixels of the target, origin at the top left; SV_Position.xy is
// the pixel center, so the pixel's top left is SV_Position.xy − 0.5.

cbuffer Markers : register(b0)
{
    float2 InverseSize;   // 1 / target size
    uint First;           // the step's first record in Instances
    uint Unused;
};

struct Marker
{
    float2 Center;
    float Size;           // the diameter, the side of a square, or the span of a cross's arms
    uint Kind;            // the shape in the low 8 bits (0 disc, 1 square, 2 cross); a cross's stroke above them, in 1/16 pixel
    float4 Color;         // premultiplied, 0..1
};

StructuredBuffer<Marker> Instances : register(t0);

struct MarkerVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Center : CENTER;
    nointerpolation float2 Extent : EXTENT;   // the half size, and half a cross's stroke
    nointerpolation uint Shape : SHAPE;
    nointerpolation float4 Color : COLOR;
};

// Corners 0..3 of a triangle strip: (0,0) (0,1) (1,0) (1,1) in (x, y) of the widened box.
MarkerVertex MarkerVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    Marker marker = Instances[First + instance];
    float radius = marker.Size * 0.5;

    // Half a pixel more than the shape: the disc's edge is half a pixel wide.
    float2 low = floor(marker.Center - radius - 0.5);
    float2 high = ceil(marker.Center + radius + 0.5);
    float2 corner = float2((vertex & 2) != 0 ? high.x : low.x, (vertex & 1) != 0 ? high.y : low.y);
    MarkerVertex output;
    output.Position = float4(corner.x * InverseSize.x * 2 - 1, 1 - corner.y * InverseSize.y * 2, 0, 1);
    output.Center = marker.Center;
    output.Extent = float2(radius, (marker.Kind >> 8) * (0.5 / 16.0));
    output.Shape = marker.Kind & 0xFF;
    output.Color = marker.Color;
    return output;
}

// The area the pixel [pixel, pixel + 1) shares with the box around the center with the given half sizes.
float BoxCoverage(float2 pixel, float2 center, float2 halfSize)
{
    float2 overlap = saturate(min(pixel + 1, center + halfSize) - max(pixel, center - halfSize));
    return overlap.x * overlap.y;
}

float4 MarkerPS(MarkerVertex input) : SV_Target
{
    float radius = input.Extent.x;
    float coverage;
    if (input.Shape == 0)
    {
        // A disc: the edge crosses a pixel in a band half a pixel to each side of the radius.
        coverage = saturate(radius + 0.5 - length(input.Position.xy - input.Center));
    }
    else
    {
        float2 pixel = floor(input.Position.xy);
        if (input.Shape == 1)
        {
            coverage = BoxCoverage(pixel, input.Center, float2(radius, radius));
        }
        else
        {
            // A cross: the two bars, less the square they share.
            float stroke = input.Extent.y;
            coverage =
                BoxCoverage(pixel, input.Center, float2(radius, stroke))
                + BoxCoverage(pixel, input.Center, float2(stroke, radius))
                - BoxCoverage(pixel, input.Center, float2(stroke, stroke));
        }
    }

    return input.Color * coverage;
}

// Resident markers (design §5.5.2 "GPU での写し方と精度", row R3a): the points stay on the GPU, each as its position in its
// cell in units of 2^-32 of the cell's extent and the cell's number. A cell's record gives, for this frame, a base position
// in the cell, the pixel of the base, and the pixels of one unit. The distance from the base is taken in integers before
// it becomes a float: the points of a view lie within a screen of the base, so the float holds it exactly enough at any zoom.

cbuffer ResidentMarkers : register(b1)
{
    float2 ResidentInverseSize;   // 1 / target size
    uint ResidentFirst;           // the run's first point in Points
    uint ResidentCells;           // where the part's cells begin in Cells
    float ResidentSize;           // the markers' size
    uint ResidentKind;            // as Marker.Kind
    uint ResidentSwap;            // not 0 when x runs down the target: a flipped plot
    uint ResidentUnused;
    float4 ResidentColor;         // premultiplied, 0..1
    float2 ResidentClampLow;      // the least and the greatest pixel of a center on each of the data's axes
    float2 ResidentClampHigh;
};

struct ResidentPoint
{
    uint X;
    uint Y;
    uint Cell;
};

struct ResidentCell
{
    uint2 Base;
    float2 Pixel;
    float2 Scale;
};

StructuredBuffer<ResidentPoint> Points : register(t1);
StructuredBuffer<ResidentCell> Cells : register(t2);

// The signed distance of two unsigned positions, taken in integers before the conversion.
float UnitsFrom(uint position, uint base)
{
    return position >= base ? (float)(position - base) : -(float)(base - position);
}

// A run has one look, so the size, the shape, and the color reach the pixel shader from the run's constants, not through
// every vertex: a frame of 1e7 points has 4 × 10^7 vertices, and with the look carried by each vertex its GPU time was
// 12.8 ms where it is 7.3 ms without (discs of 2 pixels, the whole view; Issue #438).
struct ResidentVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Center : CENTER;
};

ResidentVertex ResidentMarkerVS(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    ResidentPoint p = Points[ResidentFirst + instance];
    ResidentCell cell = Cells[ResidentCells + p.Cell];
    float2 along = cell.Pixel + float2(UnitsFrom(p.X, cell.Base.x), UnitsFrom(p.Y, cell.Base.y)) * cell.Scale;
    along = clamp(along, ResidentClampLow, ResidentClampHigh);
    float2 center = ResidentSwap != 0 ? along.yx : along;
    float reach = ResidentSize * 0.5 + 0.5;
    float2 low = floor(center - reach);
    float2 high = ceil(center + reach);
    float2 corner = float2((vertex & 2) != 0 ? high.x : low.x, (vertex & 1) != 0 ? high.y : low.y);
    ResidentVertex output;
    output.Position = float4(corner.x * ResidentInverseSize.x * 2 - 1, 1 - corner.y * ResidentInverseSize.y * 2, 0, 1);
    output.Center = center;
    return output;
}

float4 ResidentMarkerPS(ResidentVertex input) : SV_Target
{
    float radius = ResidentSize * 0.5;
    uint shape = ResidentKind & 0xFF;
    float coverage;
    if (shape == 0)
    {
        coverage = saturate(radius + 0.5 - length(input.Position.xy - input.Center));
    }
    else
    {
        float2 pixel = floor(input.Position.xy);
        if (shape == 1)
        {
            coverage = BoxCoverage(pixel, input.Center, float2(radius, radius));
        }
        else
        {
            float stroke = (ResidentKind >> 8) * (0.5 / 16.0);
            coverage =
                BoxCoverage(pixel, input.Center, float2(radius, stroke))
                + BoxCoverage(pixel, input.Center, float2(stroke, radius))
                - BoxCoverage(pixel, input.Center, float2(stroke, stroke));
        }
    }

    return ResidentColor * coverage;
}
