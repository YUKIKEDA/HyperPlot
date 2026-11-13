// Point markers (design §5.1, rows R3a and R3f). One quad per marker: MarkerVS makes it from SV_VertexID over the marker's box
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
    float Size;           // the diameter: the side of the box every shape fits in
    uint Kind;            // the shape in the low 8 bits (0 disc, 1 square, 2 cross, 3 triangle, 4 diamond, 5 x); the stroke of a
                          // cross or an x above them, in 1/16 pixel
    float4 Color;         // premultiplied, 0..1
};

StructuredBuffer<Marker> Instances : register(t0);

struct MarkerVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 Center : CENTER;
    nointerpolation float2 Extent : EXTENT;   // the half size, and half the stroke of a cross or an x
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

// The share of the pixel whose center is at position that a marker covers (design §5.1 "形", "行 R3f の形"): the shape
// fits in the box of the half size radius around center, and stroke is half the stroke of a cross or an x.
float ShapeCoverage(uint shape, float2 position, float2 center, float radius, float stroke)
{
    if (shape == 0)
    {
        // A disc: the edge crosses a pixel in a band half a pixel to each side of the radius.
        return saturate(radius + 0.5 - length(position - center));
    }

    float2 pixel = floor(position);
    if (shape == 1)
    {
        return BoxCoverage(pixel, center, float2(radius, radius));
    }

    if (shape == 2)
    {
        // A cross: the two bars, less the square they share.
        return BoxCoverage(pixel, center, float2(radius, stroke))
            + BoxCoverage(pixel, center, float2(stroke, radius))
            - BoxCoverage(pixel, center, float2(stroke, stroke));
    }

    float2 from = position - center;

    // How far the pixel's center lies outside the box. A sharp corner's sides alone would cover pixels past the corner,
    // beyond the marker's quad: the box's sides end the shape there.
    float box = max(abs(from.x), abs(from.y)) - radius;
    if (shape == 3)
    {
        // A triangle that points up: its apex at the middle of the box's top side, its base the box's bottom side. The
        // signed distance to the nearest of the two slanted sides, whose slope is 2, and of the box.
        float outside = max(box, (2 * abs(from.x) - (from.y + radius)) * 0.4472135955);
        return saturate(0.5 - outside);
    }

    if (shape == 4)
    {
        // A diamond: its corners at the middles of the box's sides.
        return saturate(0.5 - max(box, (abs(from.x) + abs(from.y) - radius) * 0.7071067812));
    }

    // An x: the bands along the box's two diagonals, cut by the box.
    float down = saturate(0.5 - (abs(from.x - from.y) * 0.7071067812 - stroke));
    float up = saturate(0.5 - (abs(from.x + from.y) * 0.7071067812 - stroke));
    return max(down, up) * BoxCoverage(pixel, center, float2(radius, radius));
}

float4 MarkerPS(MarkerVertex input) : SV_Target
{
    return input.Color * ShapeCoverage(input.Shape, input.Position.xy, input.Center, input.Extent.x, input.Extent.y);
}

// Resident markers (design §5.5.2 "GPU での写し方と精度", row R3a): the points stay on the GPU, each as its position in its
// cell in units of 2^-32 of the cell's extent and the cell's number. A cell's record gives, for this frame, a base position
// in the cell, the pixel of the base, and the pixels of one unit. The distance from the base is taken in integers before
// it becomes a float: the points of a view lie within a screen of the base, so the float holds it exactly enough at any zoom.

cbuffer ResidentMarkers : register(b1)
{
    float2 ResidentInverseSize;   // 1 / target size
    uint ResidentCells;           // where the part's cells begin in Cells
    uint ResidentFirst;           // spike R3b2: the call's first point in the page
    float ResidentSize;           // the markers' size
    uint ResidentKind;            // as Marker.Kind
    uint ResidentSwap;            // not 0 when x runs down the target: a flipped plot
    uint ResidentUnused1;
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
    float coverage = ShapeCoverage(
        ResidentKind & 0xFF,
        input.Position.xy,
        input.Center,
        ResidentSize * 0.5,
        (ResidentKind >> 8) * (0.5 / 16.0));
    return ResidentColor * coverage;
}
